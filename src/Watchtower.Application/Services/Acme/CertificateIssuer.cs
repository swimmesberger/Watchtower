using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application.Services.Acme;

/// <summary>How one attempt to obtain a certificate for one host ended.</summary>
public abstract record IssueOutcome {
    private IssueOutcome() { }

    /// <summary>A certificate was obtained and installed; it is being served from this moment on.</summary>
    public sealed record Issued(DateTimeOffset NotBefore, DateTimeOffset NotAfter, string Issuer) : IssueOutcome;

    /// <summary>
    /// The domain does not resolve yet, so nothing was asked of the CA. Its own outcome rather than a
    /// failure: it is the normal state of a route created before its DNS record, it needs a different
    /// sentence on the Routes page, and — crucially — it must not consume a validation attempt.
    /// </summary>
    public sealed record AwaitingDns(string Detail) : IssueOutcome;

    /// <summary>The attempt failed. <paramref name="Class"/> decides how long before the next one.</summary>
    public sealed record Failed(string Detail, AcmeFailureClass Class, TimeSpan? RetryAfter) : IssueOutcome;
}

/// <summary>
/// Runs one ACME order end to end for one host — ADR-0022: DNS preflight, account, order,
/// HTTP-01, CSR, finalize, download, install. Stateless with respect to scheduling: it makes exactly one
/// attempt and reports how it went, and <see cref="CertificateManager"/> owns everything about when.
/// </summary>
/// <remarks>
/// The split is what makes issuance testable. Everything about retrying, backing off, jittering and
/// concurrency lives in the manager and is a function of a clock; everything about the protocol lives
/// here and is exercised end-to-end against an in-process CA.
/// <para>
/// One order per host, never a multi-identifier one. A single order covering twenty hosts fails as a unit
/// when one of them fails to validate — which, for a deployment where one customer's DNS is
/// misconfigured, means nineteen sites lose their renewal because of the twentieth.
/// </para>
/// </remarks>
public sealed class CertificateIssuer(
    CertificateStore store,
    AcmeHttpChallengeStore challenges,
    DnsPreflight dns,
    YarpListenerState listener,
    AuditLog audit,
    TimeProvider time,
    ILogger<CertificateIssuer> logger) {
    /// <summary>The audit category the certificate machinery writes under — the Routes page's slice.</summary>
    internal const string AuditCategory = "proxy";

    /// <summary>How long an authorization may stay pending before the attempt is abandoned.</summary>
    private static readonly TimeSpan AuthorizationTimeout = TimeSpan.FromSeconds(120);

    /// <summary>How long finalization may take before the attempt is abandoned.</summary>
    private static readonly TimeSpan OrderTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Clock disagreement with the CA past which the log says so.</summary>
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Detail strings are operator-facing and land in a status column; they are not transcripts.</summary>
    private const int MaxDetailLength = 500;

    /// <summary>
    /// Attempts one issuance for <paramref name="host"/> against <paramref name="session"/>'s CA.
    /// </summary>
    /// <remarks>
    /// Never throws for a protocol or network reason — every one of those is an
    /// <see cref="IssueOutcome.Failed"/> carrying the class the caller schedules on. Cancellation still
    /// propagates: a host dropping out of the desired set mid-order is not a failure to record.
    /// </remarks>
    public async Task<IssueOutcome> IssueAsync(string host, AcmeSession session, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(session);

        try {
            return await RunAsync(host, session, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (OperationCanceledException ex) {
            // Not our token, so this is HttpClient's own timeout, which surfaces as a
            // TaskCanceledException rather than a TimeoutException. A CA that did not answer in thirty
            // seconds is a transport problem — falling through to the generic handler below would log it
            // as an unexpected failure and send whoever reads the log looking for a bug.
            return Failed(
                $"The ACME server did not respond within the request timeout: {ex.Message}",
                AcmeFailureClass.Transport, null);
        } catch (AcmeException ex) {
            logger.LogWarning(ex, "The CA refused the certificate order for {Host}.", host);
            return Failed(ex.Message, Classify(ex), ex.RetryAfter);
        } catch (TimeoutException ex) {
            return Failed(ex.Message, AcmeFailureClass.Transport, null);
        } catch (HttpRequestException ex) {
            return Failed($"Could not reach the ACME directory: {ex.Message}", AcmeFailureClass.Transport, null);
        } catch (Exception ex) {
            logger.LogError(ex, "Unexpected failure while obtaining a certificate for {Host}.", host);
            return Failed(ex.Message, AcmeFailureClass.Transport, null);
        }
    }

    private async Task<IssueOutcome> RunAsync(string host, AcmeSession session, CancellationToken ct) {
        // ── 0. DNS preflight ──────────────────────────────────────────────────
        // Before any ACME traffic at all. A host whose DNS is not pointed here cannot pass HTTP-01, and
        // asking anyway spends one of the five failed validations per hostname per hour that Let's
        // Encrypt allows — on a question we could answer ourselves for free.
        var addresses = await dns.ResolveAsync(host, ct);
        if (addresses.Count == 0)
            return new IssueOutcome.AwaitingDns(
                $"{host} does not resolve yet; point an A/AAAA record at this host.");

        var client = session.Client;

        // ── 1–2. Directory and account ────────────────────────────────────────
        var directory = await client.GetDirectoryAsync(session.DirectoryUrl, ct);
        var isFirstRegistration = session.Account.AccountUrl is null;
        await client.EnsureAccountAsync(session.ContactEmail, session.EabKeyId, session.EabHmacKey, ct);
        if (isFirstRegistration && session.Account.AccountUrl is not null)
            await audit.RecordAsync(
                AuditCategory, "acme.account.create", session.DirectoryUrl.Host,
                directory.Meta?.TermsOfService is { Length: > 0 } terms ? $"terms of service: {terms}" : null,
                ct: ct);

        WarnOnClockSkew(client);

        // ── 3. Order ──────────────────────────────────────────────────────────
        var (order, orderUrl) = await client.NewOrderAsync(host, ct);

        // A `ready` order is one whose authorizations are already valid — a re-order inside the CA's
        // authorization reuse window, which is 30 days at Let's Encrypt. Nothing to prove; go straight
        // to the CSR. This is the ordinary path for a renewal, not an edge case.
        if (!string.Equals(order.Status, "ready", StringComparison.Ordinal)) {
            if (string.Equals(order.Status, "invalid", StringComparison.Ordinal))
                return Failed(
                    order.Error?.Detail ?? "The CA rejected the order.", AcmeFailureClass.Validation, null);

            // ── 4. Authorization + challenge selection ────────────────────────
            var authorizationUrl = order.Authorizations.FirstOrDefault();
            if (authorizationUrl is null)
                return Failed("The CA created an order with no authorizations.", AcmeFailureClass.Terminal, null);

            var authorization = await client.GetAuthorizationAsync(authorizationUrl, ct);
            if (!string.Equals(authorization.Status, "valid", StringComparison.Ordinal)) {
                var outcome = await ValidateAsync(host, client, authorization, authorizationUrl, session, ct);
                if (outcome is not null) return outcome;
            }

            // ── 6b. Wait for the order to leave pending ───────────────────────
            // A valid authorization makes the order ready, but the CA moves it on its own schedule.
            order = await client.PollOrderAsync(orderUrl, OrderTimeout, ct);
            if (string.Equals(order.Status, "invalid", StringComparison.Ordinal))
                return Failed(
                    order.Error?.Detail ?? "The CA rejected the order after validation.",
                    AcmeFailureClass.Validation, null);
        }

        // ── 7. CSR ────────────────────────────────────────────────────────────
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName(""), key, HashAlgorithmName.SHA256);
        var sans = new SubjectAlternativeNameBuilder();
        sans.AddDnsName(host);
        request.CertificateExtensions.Add(sans.Build());
        // Deliberately no common name. A CN is capped at 64 characters and a tenant host easily exceeds
        // that — `X500DistinguishedName` would refuse to encode it — while Let's Encrypt (and the CA/B
        // baseline requirements since 2021) ignores the subject entirely and issues from the SAN. An
        // empty subject is therefore both the only thing that works for long names and what the CA wants.
        var csr = request.CreateSigningRequest();

        // ── 8. Finalize ───────────────────────────────────────────────────────
        order = await client.FinalizeAsync(order.Finalize, csr, ct);
        if (order.Status is not "valid")
            // The finalized-order poller, not the one used above: after the CSR is in, `ready` means the
            // CA has taken it and not yet issued, so stopping there would abandon an order that was
            // about to succeed.
            order = await client.PollFinalizedOrderAsync(orderUrl, OrderTimeout, ct);
        if (!string.Equals(order.Status, "valid", StringComparison.Ordinal))
            return Failed(
                order.Error?.Detail ?? $"The CA left the order in state '{order.Status}'.",
                string.Equals(order.Status, "invalid", StringComparison.Ordinal)
                    ? AcmeFailureClass.Validation
                    : AcmeFailureClass.Transport,
                null);
        if (string.IsNullOrWhiteSpace(order.Certificate))
            return Failed("The CA marked the order valid without a certificate URL.", AcmeFailureClass.Transport, null);

        // ── 9–10. Download and install ────────────────────────────────────────
        var pem = await client.DownloadCertificateAsync(order.Certificate, ct);
        // Read before installing so the audit trail and the outcome describe the certificate that was
        // actually issued rather than what was asked for.
        var renewal = store.Find(host) is not null;
        await store.InstallAsync(host, pem, key, ct);

        var entry = store.Find(host);
        if (entry is null)
            return Failed(
                "The issued certificate was written but could not be served; check the certificate directory.",
                AcmeFailureClass.Transport, null);

        await audit.RecordAsync(
            AuditCategory, renewal ? "cert.renew" : "cert.issue", host,
            $"issuer {entry.IssuerCommonName} · expires {entry.NotAfter:u}", ct: ct);
        logger.LogInformation(
            "Issued a certificate for {Host} from {Issuer}, valid until {NotAfter:u}.",
            host, entry.IssuerCommonName, entry.NotAfter);

        return new IssueOutcome.Issued(entry.NotBefore, entry.NotAfter, entry.IssuerCommonName);
    }

    /// <summary>
    /// Publishes the HTTP-01 answer, self-checks it, triggers validation and waits. Returns null when the
    /// identifier was validated, or the outcome to report when it was not.
    /// </summary>
    private async Task<IssueOutcome?> ValidateAsync(
        string host,
        AcmeClient client,
        AcmeAuthorization authorization,
        string authorizationUrl,
        AcmeSession session,
        CancellationToken ct) {
        var challenge = authorization.Challenges.FirstOrDefault(
            c => string.Equals(c.Type, "http-01", StringComparison.Ordinal));
        if (challenge is null || string.IsNullOrWhiteSpace(challenge.Token))
            return Failed("The CA does not offer http-01 for this identifier.", AcmeFailureClass.Terminal, null);

        var keyAuthorization = AcmeJws.KeyAuthorization(challenge.Token, session.Account.Key);

        // ── 5. Publish, then prove to ourselves it is answerable ──────────────
        // The `await using` is what retracts the row on every path out, including the throwing ones — a
        // challenge left answerable after its order settled is a token any stranger can fetch. It is a
        // row rather than process state since ADR-0024, so that the CA's validation request can land on
        // any instance; the write costs one insert per order, on a path that is already talking to a CA
        // over the network.
        await using var published = await challenges.PublishAsync(
            challenge.Token, keyAuthorization, host, ct: ct);

        if (session.SelfCheckEnabled && listener.LocalHttpAddress is { } localAddress) {
            var why = await SelfCheckAsync(localAddress, host, challenge.Token, keyAuthorization, ct);
            if (why is not null)
                return Failed(
                    $"Watchtower cannot serve its own ACME challenge for {host}: {why}. "
                    + "Is the HTTP listener reachable?",
                    AcmeFailureClass.Terminal, null);
        }

        // ── 6. Trigger and wait ───────────────────────────────────────────────
        await client.TriggerChallengeAsync(challenge.Url, ct);
        var settled = await client.PollAuthorizationAsync(authorizationUrl, AuthorizationTimeout, ct);
        if (string.Equals(settled.Status, "valid", StringComparison.Ordinal)) return null;

        var detail = settled.Challenges
                         .FirstOrDefault(c => string.Equals(c.Type, "http-01", StringComparison.Ordinal))?.Error?.Detail
                     ?? $"The CA could not validate {host} (authorization {settled.Status}).";
        return Failed(detail, AcmeFailureClass.Validation, null);
    }

    /// <summary>
    /// Fetches the challenge from Watchtower's own HTTP listener, with the public host in the
    /// <c>Host</c> header, exactly as the CA is about to.
    /// </summary>
    /// <remarks>
    /// This is the single highest-value check in the whole flow, because of the asymmetry in what a
    /// failure costs: an unanswerable challenge discovered here costs nothing, and discovered by the CA
    /// costs one of five hourly validation failures per hostname — five of which lock the host out for
    /// an hour. It catches the middleware being mis-ordered, the listener not being bound, and the
    /// answer being served with the wrong body.
    /// <para>
    /// It does <em>not</em> prove the CA can reach us: that would need a request from outside, which is
    /// the CA's job. Which is also why a failure is <see cref="AcmeFailureClass.Terminal"/> — the
    /// listener is process state, so retrying without a restart or a config change cannot change it.
    /// </para>
    /// </remarks>
    private async Task<string?> SelfCheckAsync(
        string localAddress, string host, string token, string expected, CancellationToken ct) {
        try {
            // Its own client, with no proxy and a short timeout: this is a loopback request that either
            // answers immediately or is broken, and inheriting an ambient proxy would send it elsewhere.
            using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
            using var probe = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{localAddress.TrimEnd('/')}/.well-known/acme-challenge/{token}");
            // The header the whole dispatch depends on: the listener answers on 127.0.0.1, but every
            // routing decision downstream is made on this name.
            request.Headers.Host = host;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));

            using var response = await probe.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return $"the local listener answered {(int)response.StatusCode}";
            var body = await response.Content.ReadAsStringAsync(ct);
            return string.Equals(body, expected, StringComparison.Ordinal)
                ? null
                : "the local listener answered with the wrong content";
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            return ex.Message;
        }
    }

    /// <summary>
    /// Maps a CA problem type onto how hard to retry. <c>badNonce</c> is absent on purpose — the client
    /// retries it internally and it never reaches here.
    /// </summary>
    private static AcmeFailureClass Classify(AcmeException ex) => ex switch {
        _ when ex.IsType(AcmeProblemTypes.RateLimited) => AcmeFailureClass.RateLimited,
        _ when ex.IsType(AcmeProblemTypes.Connection) => AcmeFailureClass.Validation,
        _ when ex.IsType(AcmeProblemTypes.Dns) => AcmeFailureClass.Validation,
        _ when ex.IsType(AcmeProblemTypes.Unauthorized) => AcmeFailureClass.Validation,
        _ when ex.IsType(AcmeProblemTypes.IncorrectResponse) => AcmeFailureClass.Validation,
        _ when ex.IsType(AcmeProblemTypes.UserActionRequired) => AcmeFailureClass.Terminal,
        _ when ex.IsType(AcmeProblemTypes.Malformed) => AcmeFailureClass.Terminal,
        _ when ex.IsType(AcmeProblemTypes.UnsupportedIdentifier) => AcmeFailureClass.Terminal,
        _ when ex.IsType(AcmeProblemTypes.RejectedIdentifier) => AcmeFailureClass.Terminal,
        _ when ex.IsType(AcmeProblemTypes.ExternalAccountRequired) => AcmeFailureClass.Terminal,
        _ => AcmeFailureClass.Transport,
    };

    /// <summary>
    /// A CA whose clock disagrees with ours issues certificates that look not-yet-valid or already
    /// expired here, which surfaces as a browser error nothing in the issuance log explains. One line
    /// when it happens turns that into a diagnosis.
    /// </summary>
    private void WarnOnClockSkew(AcmeClient client) {
        if (client.LastServerDate is not { } served) return;
        var skew = served - time.GetUtcNow();
        if (skew.Duration() <= MaxClockSkew) return;
        logger.LogWarning(
            "This host's clock differs from the ACME server's by {Skew}. Certificates may be rejected as "
            + "not yet valid or already expired; check NTP.", skew.Duration());
    }

    private static IssueOutcome.Failed Failed(string detail, AcmeFailureClass cls, TimeSpan? retryAfter) =>
        new(detail.Length > MaxDetailLength ? detail[..MaxDetailLength] : detail, cls, retryAfter);
}

/// <summary>
/// Everything an issuance needs about the CA it is talking to: the client (which owns the account key
/// and the nonce pool) and the settings that key the account.
/// </summary>
/// <remarks>
/// Passed in per attempt rather than injected because the settings are runtime-switchable: an operator
/// pointing Watchtower at a different directory gets a different account, a different client and a
/// different nonce pool, and the certificate manager rebuilds all of it in one step. Handing the issuer
/// a value makes that swap atomic from its point of view — an order in flight finishes against the CA it
/// started with.
/// </remarks>
/// <param name="Client">The ACME client, whose lifetime the manager owns.</param>
/// <param name="Account">The account key the client signs with, for the key authorization.</param>
public sealed record AcmeSession(
    AcmeClient Client,
    AcmeAccountKey Account,
    Uri DirectoryUrl,
    string? ContactEmail,
    string? EabKeyId,
    string? EabHmacKey,
    bool SelfCheckEnabled) : IDisposable {
    public void Dispose() {
        Client.Dispose();
        Account.Dispose();
    }
}
