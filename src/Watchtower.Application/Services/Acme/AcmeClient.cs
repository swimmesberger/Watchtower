using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services.Acme;

/// <summary>
/// A minimal RFC 8555 client: directory discovery, account registration (with optional External Account
/// Binding), one-identifier orders, HTTP-01 challenge triggering, finalization and certificate download.
/// ADR-0022.
/// </summary>
/// <remarks>
/// Hand-written over <see cref="HttpClient"/> and source-generated JSON, for the reasons ADR-0022 records:
/// the maintained .NET ACME libraries either carry Newtonsoft.Json or ship a compiled-in public-suffix
/// list that goes stale, and the protocol surface Watchtower needs is one order shape and six requests.
/// The shape follows <see cref="CloudflareApiClient"/> — a private send method, the server's own error
/// text surfaced on the exception, no state beyond what the protocol forces.
/// <para>
/// What the protocol does force is a <em>nonce pool</em>. Every POST must carry a nonce the CA issued and
/// has not seen before (§6.5), and every response — success or failure — carries a fresh one in
/// <c>Replay-Nonce</c>. Harvesting those is what keeps a run of orders from doubling its request count
/// with <c>newNonce</c> round-trips; a stale one comes back as <c>badNonce</c>, which is explicitly
/// retryable and is retried here rather than surfaced.
/// </para>
/// <para>
/// One instance per configured directory URL, because it holds the account key and the nonce pool for
/// that CA. The certificate manager recreates it when the ACME settings change.
/// </para>
/// </remarks>
public sealed class AcmeClient : IDisposable {
    /// <summary>How many times a single request is re-signed after a <c>badNonce</c> before giving up.</summary>
    private const int MaxNonceRetries = 3;

    /// <summary>How long the pollers wait between reads when the CA suggests nothing itself.</summary>
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(2);

    private const string JoseContentType = "application/jose+json";
    private const string PemChainContentType = "application/pem-certificate-chain";
    private const string ProblemContentType = "application/problem+json";

    private readonly HttpClient _http;
    private readonly AcmeAccountKey _account;
    private readonly TimeProvider _time;
    private readonly ILogger<AcmeClient> _logger;
    private readonly ConcurrentQueue<string> _nonces = new();
    private readonly ConcurrentDictionary<string, AcmeDirectory> _directories = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _accountGate = new(1, 1);

    private AcmeDirectory? _directory;
    private string? _contactEmail;
    private string? _eabKeyId;
    private string? _eabHmacKey;
    private bool _loggedTerms;
    private bool _disposed;

    public AcmeClient(HttpClient http, AcmeAccountKey account, TimeProvider time, ILogger<AcmeClient> logger) {
        _http = http;
        _account = account;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// The <c>Date</c> of the last response the CA sent. A JWS is not timestamped, but a CA whose clock
    /// disagrees with ours by more than a few minutes will reject certificates as not-yet-valid — so the
    /// issuer compares this against the local clock and warns, which turns an inexplicable failure into a
    /// one-line diagnosis.
    /// </summary>
    public DateTimeOffset? LastServerDate { get; private set; }

    // ── Directory ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Fetches (and caches) the directory. Cached per URL and for the client's lifetime: a CA's endpoint
    /// layout is stable, and re-reading it on every order would double the request count for nothing.
    /// </summary>
    public async Task<AcmeDirectory> GetDirectoryAsync(Uri directoryUrl, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(directoryUrl);
        var key = directoryUrl.ToString();
        if (_directories.TryGetValue(key, out var cached)) {
            _directory = cached;
            return cached;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, directoryUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, ct);
        var body = await ReadAsync(response, ct);
        if (!response.IsSuccessStatusCode) throw Failure(response, body, $"GET {directoryUrl}");

        var directory = Deserialize(body, AcmeJsonContext.Default.AcmeDirectory, key)
            ?? throw new AcmeException(null, response.StatusCode, null, $"The ACME directory at {directoryUrl} is empty.");
        if (string.IsNullOrWhiteSpace(directory.NewNonce) || string.IsNullOrWhiteSpace(directory.NewAccount)
            || string.IsNullOrWhiteSpace(directory.NewOrder))
            throw new AcmeException(
                null, response.StatusCode, null,
                $"The ACME directory at {directoryUrl} is missing newNonce, newAccount or newOrder.");

        _directories[key] = directory;
        _directory = directory;
        return directory;
    }

    private AcmeDirectory Directory => _directory
        ?? throw new InvalidOperationException("The ACME directory must be fetched before any other request.");

    // ── Account ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensures this key is registered and returns the account URL used as the JWS <c>kid</c>. A key that
    /// is already registered costs no request at all — the account URL is on disk.
    /// </summary>
    /// <param name="contactEmail">
    /// The address the CA sends expiry warnings to. Blank means the <c>contact</c> member is omitted
    /// entirely, which is not the same as sending an empty list.
    /// </param>
    public async Task<string> EnsureAccountAsync(
        string? contactEmail, string? eabKeyId, string? eabHmacKey, CancellationToken ct) {
        // Remembered so the accountDoesNotExist recovery below can register again on its own, without
        // every caller having to hold on to the credentials for a case that almost never happens.
        _contactEmail = contactEmail;
        _eabKeyId = eabKeyId;
        _eabHmacKey = eabHmacKey;

        if (_account.AccountUrl is { } known) return known;

        await _accountGate.WaitAsync(ct);
        try {
            // Re-checked inside the gate: several hosts' orders start at once on a first run, and
            // registering the same key twice is legal but wasteful.
            if (_account.AccountUrl is { } raced) return raced;
            return await RegisterAsync(ct);
        } finally {
            _accountGate.Release();
        }
    }

    /// <summary>
    /// Registers the account key (RFC 8555 §7.3). The response's <c>Location</c> is the account URL,
    /// whether the CA answered 201 (created) or 200 (this key was already registered) — the latter is
    /// how the protocol makes registration idempotent.
    /// </summary>
    private async Task<string> RegisterAsync(CancellationToken ct) {
        var directory = Directory;
        if (directory.Meta?.ExternalAccountRequired == true && string.IsNullOrWhiteSpace(_eabKeyId))
            _logger.LogWarning(
                "The ACME directory requires an External Account Binding, but none is configured "
                + "(Proxy:Yarp:AcmeEabKeyId / AcmeEabHmacKey). Registration will be refused.");

        JsonElement? binding = null;
        if (!string.IsNullOrWhiteSpace(_eabKeyId) && !string.IsNullOrWhiteSpace(_eabHmacKey)) {
            var json = AcmeJws.ExternalAccountBinding(
                _account.Key, directory.NewAccount, _eabKeyId.Trim(), _eabHmacKey.Trim());
            // Parsed into a JsonElement and re-attached rather than re-serialized from records: the
            // signature inside covers those exact bytes.
            using var document = JsonDocument.Parse(json);
            binding = document.RootElement.Clone();
        }

        var email = _contactEmail?.Trim();
        var payload = JsonSerializer.Serialize(
            new NewAccountPayload {
                TermsOfServiceAgreed = true,
                Contact = string.IsNullOrEmpty(email) ? null : [$"mailto:{email}"],
                ExternalAccountBinding = binding,
            },
            AcmeJsonContext.Default.NewAccountPayload);

        // The one request signed with `jwk` instead of `kid`: there is no account URL yet to name.
        var result = await SendAsync(directory.NewAccount, payload, signWithJwk: true, ct: ct);
        var location = result.Location
            ?? throw new AcmeException(
                null, result.Status, null, "The CA registered the account without returning a Location header.");

        _account.SetAccountUrl(location);
        if (!_loggedTerms) {
            _loggedTerms = true;
            _logger.LogInformation(
                "Registered an ACME account with {Directory}{Terms}.",
                directory.NewAccount,
                directory.Meta?.TermsOfService is { Length: > 0 } terms
                    ? $" (terms of service: {terms})"
                    : "");
        }
        return location;
    }

    // ── Orders ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an order for one DNS identifier and returns it with its own URL (the <c>Location</c>
    /// header), which is what the order poller reads back.
    /// </summary>
    public async Task<(AcmeOrder Order, string OrderUrl)> NewOrderAsync(string domain, CancellationToken ct) {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        var payload = JsonSerializer.Serialize(
            new NewOrderPayload { Identifiers = [new AcmeIdentifier { Type = "dns", Value = domain }] },
            AcmeJsonContext.Default.NewOrderPayload);

        var result = await SendAsync(Directory.NewOrder, payload, ct: ct);
        var order = Parse(result, AcmeJsonContext.Default.AcmeOrder, "order");
        var url = result.Location
            ?? throw new AcmeException(
                null, result.Status, null, "The CA created the order without returning a Location header.");
        return (order, url);
    }

    /// <summary>Reads an authorization (POST-as-GET, §7.5).</summary>
    public async Task<AcmeAuthorization> GetAuthorizationAsync(string url, CancellationToken ct) =>
        (await ReadAuthorizationAsync(url, ct)).Document;

    private async Task<Polled<AcmeAuthorization>> ReadAuthorizationAsync(string url, CancellationToken ct) {
        var response = await SendAsync(url, payloadJson: null, ct: ct);
        return new Polled<AcmeAuthorization>(
            Parse(response, AcmeJsonContext.Default.AcmeAuthorization, "authorization"), response.RetryAfter);
    }

    /// <summary>
    /// Tells the CA the challenge is ready to be validated (§7.5.1).
    /// </summary>
    /// <remarks>
    /// The payload is <c>{}</c> — an empty JSON <em>object</em>, and emphatically not the empty payload
    /// of a POST-as-GET. The distinction is the difference between "validate this challenge" and "show me
    /// this challenge": a CA that receives the latter answers with the challenge unchanged and never
    /// schedules validation, which is an order that hangs at <c>pending</c> until it expires.
    /// </remarks>
    public async Task<AcmeChallenge> TriggerChallengeAsync(string challengeUrl, CancellationToken ct) =>
        Parse(await SendAsync(challengeUrl, "{}", ct: ct), AcmeJsonContext.Default.AcmeChallenge, "challenge");

    /// <summary>
    /// Polls an authorization until it leaves <c>pending</c> (§7.1.6). Every other status —
    /// <c>valid</c>, <c>invalid</c>, <c>expired</c>, <c>deactivated</c>, <c>revoked</c> — is terminal,
    /// so the loop stops on all of them and the caller decides which mean success.
    /// </summary>
    /// <exception cref="TimeoutException">The authorization was still pending when the budget ran out.</exception>
    public Task<AcmeAuthorization> PollAuthorizationAsync(string url, TimeSpan timeout, CancellationToken ct) =>
        PollAsync(
            url, timeout, ct,
            () => ReadAuthorizationAsync(url, ct),
            a => !string.Equals(a.Status, "pending", StringComparison.Ordinal),
            a => a.Status,
            "authorization");

    /// <summary>Submits the CSR (§7.4). Returns the order as it stood immediately after finalization.</summary>
    public async Task<AcmeOrder> FinalizeAsync(string finalizeUrl, byte[] csrDer, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(csrDer);
        var payload = JsonSerializer.Serialize(
            new FinalizePayload { Csr = AcmeJws.Base64Url(csrDer) }, AcmeJsonContext.Default.FinalizePayload);
        return Parse(await SendAsync(finalizeUrl, payload, ct: ct), AcmeJsonContext.Default.AcmeOrder, "order");
    }

    /// <summary>
    /// Polls an order until it leaves <c>pending</c> and <c>processing</c> (§7.1.6) — the poller for
    /// <em>before</em> the CSR is sent.
    /// </summary>
    /// <remarks>
    /// <c>ready</c> stops this loop even though it is not the end of the state machine, and deliberately:
    /// it means "all authorizations are valid, send the CSR", which is the caller's move and not
    /// something further polling would advance. After finalizing, the same status means the opposite —
    /// the CA has taken the CSR and not yet acted on it — which is why that side has its own poller
    /// (<see cref="PollFinalizedOrderAsync"/>) rather than a flag on this one.
    /// </remarks>
    /// <exception cref="TimeoutException">The order was still in flight when the budget ran out.</exception>
    public Task<AcmeOrder> PollOrderAsync(string orderUrl, TimeSpan timeout, CancellationToken ct) =>
        PollAsync(
            orderUrl, timeout, ct,
            () => ReadOrderAsync(orderUrl, ct),
            o => o.Status is not ("pending" or "processing"),
            o => o.Status,
            "order");

    /// <summary>
    /// Polls a finalized order until it is <c>valid</c> or <c>invalid</c> — the poller for <em>after</em>
    /// the CSR is sent, and the only difference is that <c>ready</c> is no longer an exit. Here it means
    /// the CA has accepted the CSR and not yet issued, so stopping on it would abandon an order that was
    /// about to succeed; Boulder leaves orders there under load.
    /// </summary>
    /// <exception cref="TimeoutException">The order had not settled when the budget ran out.</exception>
    public Task<AcmeOrder> PollFinalizedOrderAsync(string orderUrl, TimeSpan timeout, CancellationToken ct) =>
        PollAsync(
            orderUrl, timeout, ct,
            () => ReadOrderAsync(orderUrl, ct),
            o => o.Status is "valid" or "invalid",
            o => o.Status,
            "order");

    private async Task<Polled<AcmeOrder>> ReadOrderAsync(string orderUrl, CancellationToken ct) {
        var response = await SendAsync(orderUrl, payloadJson: null, ct: ct);
        return new Polled<AcmeOrder>(
            Parse(response, AcmeJsonContext.Default.AcmeOrder, "order"), response.RetryAfter);
    }

    /// <summary>One poll read: the resource, and how long the CA asked us to wait before the next one.</summary>
    private readonly record struct Polled<T>(T Document, TimeSpan? RetryAfter);

    /// <summary>Downloads the issued chain as PEM, leaf first (§7.4.2).</summary>
    public async Task<string> DownloadCertificateAsync(string certificateUrl, CancellationToken ct) {
        var result = await SendAsync(certificateUrl, payloadJson: null, accept: PemChainContentType, ct: ct);
        if (string.IsNullOrWhiteSpace(result.Body))
            throw new AcmeException(null, result.Status, null, "The CA returned an empty certificate chain.");
        return result.Body;
    }

    // ── Polling ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The shared poll loop. The CA sets the cadence wherever it says so — §7.1.3 and §7.5.1 let it send
    /// <c>Retry-After</c> on the ordinary 200 as well as on an error, and it is the only party that knows
    /// how long its own validation queue is. Two seconds otherwise, and never a sleep past the caller's
    /// budget: a poll that would wake up after the deadline is a timeout now rather than a wasted wait.
    /// </summary>
    private async Task<T> PollAsync<T>(
        string url,
        TimeSpan timeout,
        CancellationToken ct,
        Func<Task<Polled<T>>> read,
        Func<T, bool> settled,
        Func<T, string> status,
        string what) {
        var deadline = _time.GetUtcNow() + timeout;
        while (true) {
            T current;
            TimeSpan wait;
            try {
                var polled = await read();
                current = polled.Document;
                if (settled(current)) return current;
                wait = polled.RetryAfter ?? DefaultPollInterval;
            } catch (AcmeException ex) when (ex.RetryAfter is { } retryAfter && ex.Status == HttpStatusCode.TooManyRequests) {
                // A rate limit hit mid-poll is not a failure of this order; the CA said when to come back.
                wait = retryAfter;
                current = default!;
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero || wait >= remaining)
                throw new TimeoutException(
                    $"The ACME {what} at {url} was still {(current is null ? "unavailable" : status(current))} "
                    + $"after {timeout.TotalSeconds:0} seconds.");
            await Task.Delay(wait, _time, ct);
        }
    }

    // ── Transport ─────────────────────────────────────────────────────────────

    /// <summary>
    /// One ACME response, reduced to what any caller here needs. <paramref name="RetryAfter"/> is carried
    /// off successful responses too, not only failures: §7.1.3 and §7.5.1 let a CA pace a client's
    /// polling with it, and ignoring that on a 200 is how a client turns a two-second wait into a
    /// hundred requests.
    /// </summary>
    private readonly record struct AcmeResponse(
        HttpStatusCode Status, string Body, string? Location, TimeSpan? RetryAfter);

    /// <summary>
    /// Signs and sends one ACME request, retrying a <c>badNonce</c> and recovering from a forgotten
    /// account. Every JWS is signed for exactly one nonce and one URL, so a retry has to re-sign rather
    /// than resend — which is why the payload comes in as a string and the signing happens here.
    /// </summary>
    private async Task<AcmeResponse> SendAsync(
        string url,
        string? payloadJson,
        bool signWithJwk = false,
        string accept = "application/json",
        CancellationToken ct = default) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var nonceAttempts = 0;
        var reregistered = false;

        while (true) {
            var nonce = await TakeNonceAsync(ct);
            var kid = signWithJwk ? null : _account.AccountUrl
                ?? throw new InvalidOperationException("The ACME account must be registered before this request.");
            var jws = AcmeJws.Sign(_account.Key, url, nonce, kid, payloadJson);

            using var request = new HttpRequestMessage(HttpMethod.Post, url) {
                // The media type is set WITHOUT a charset parameter, and that is load-bearing: Boulder
                // (Let's Encrypt) compares the Content-Type against the literal "application/jose+json"
                // and answers 415 malformed to anything else, including the
                // "application/jose+json; charset=utf-8" that StringContent's encoding overload emits.
                // A JWS body is base64url and JSON, so it is ASCII either way and nothing is lost.
                Content = new StringContent(jws, new MediaTypeHeaderValue(JoseContentType)),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));

            using var response = await _http.SendAsync(request, ct);
            var body = await ReadAsync(response, ct);

            if (response.IsSuccessStatusCode)
                return new AcmeResponse(
                    response.StatusCode, body, response.Headers.Location?.ToString(),
                    ParseRetryAfter(response));

            var failure = Failure(response, body, $"POST {url}");

            // §6.5: badNonce is explicitly retryable, and the response that reported it carries a fresh
            // one. The whole pool goes with it, not just the nonce that was rejected: a CA that has
            // rotated its nonce key — or been failed over to another instance — has invalidated every
            // nonce it previously issued, so keeping the rest would spend all three attempts one stale
            // nonce at a time. Only the value from this very response survives.
            if (failure.IsType(AcmeProblemTypes.BadNonce) && ++nonceAttempts < MaxNonceRetries) {
                ResetNoncePool(response);
                _logger.LogDebug("The CA rejected the nonce for {Url}; re-signing (attempt {Attempt}).", url, nonceAttempts + 1);
                continue;
            }

            // The CA no longer knows this account — the key survived a CA-side reset, or the account
            // was deactivated. Registering again is the only way forward, and it is safe: the same key
            // registers to the same account when one exists.
            if (failure.IsType(AcmeProblemTypes.AccountDoesNotExist) && !signWithJwk && !reregistered) {
                reregistered = true;
                _logger.LogWarning("The CA does not know the stored ACME account; registering again.");
                _account.ClearAccountUrl();
                await _accountGate.WaitAsync(ct);
                try {
                    if (_account.AccountUrl is null) await RegisterAsync(ct);
                } finally {
                    _accountGate.Release();
                }
                continue;
            }

            throw failure;
        }
    }

    /// <summary>
    /// A nonce from the pool, or a fresh one from <c>newNonce</c> when it is empty. HEAD rather than GET
    /// because §7.2 defines the resource that way and a HEAD costs no body.
    /// </summary>
    /// <remarks>
    /// The fetch is retried rather than asserted, because the pool is shared: two orders running
    /// concurrently can both find it empty, and the one that fetches second may have its nonce taken by
    /// the first between the enqueue and the dequeue. That is a lost race, not a CA that omitted the
    /// header, and reporting it as the latter would send an operator looking at their CA.
    /// </remarks>
    private async Task<string> TakeNonceAsync(CancellationToken ct) {
        for (var attempt = 0; attempt < MaxNonceRetries; attempt++) {
            if (_nonces.TryDequeue(out var pooled)) return pooled;

            using var request = new HttpRequestMessage(HttpMethod.Head, Directory.NewNonce);
            using var response = await _http.SendAsync(request, ct);
            CaptureResponseMetadata(response);
            if (!response.IsSuccessStatusCode)
                throw Failure(response, await response.Content.ReadAsStringAsync(ct), $"HEAD {Directory.NewNonce}");
            if (_nonces.TryDequeue(out var fresh)) return fresh;
        }

        throw new AcmeException(
            null, HttpStatusCode.OK, null,
            $"The ACME directory's newNonce endpoint ({Directory.NewNonce}) returned no usable Replay-Nonce "
            + $"header in {MaxNonceRetries} attempts.");
    }

    /// <summary>
    /// Throws the pool away and keeps only what <paramref name="response"/> carried — the
    /// <c>badNonce</c> recovery. See the call site for why the rest cannot be trusted.
    /// </summary>
    private void ResetNoncePool(HttpResponseMessage response) {
        while (_nonces.TryDequeue(out _)) { }
        if (response.Headers.TryGetValues("Replay-Nonce", out var values))
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value))
                    _nonces.Enqueue(value);
    }

    /// <summary>
    /// Reads a response body and harvests everything the client keeps off a response: the replay nonce
    /// (from failures too — §6.5 requires the CA to send one either way) and the server clock.
    /// </summary>
    private async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken ct) {
        CaptureResponseMetadata(response);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private void CaptureResponseMetadata(HttpResponseMessage response) {
        if (response.Headers.TryGetValues("Replay-Nonce", out var values))
            foreach (var value in values)
                if (!string.IsNullOrWhiteSpace(value))
                    _nonces.Enqueue(value);
        if (response.Headers.Date is { } date) LastServerDate = date;
    }

    /// <summary>
    /// Turns a failed response into an <see cref="AcmeException"/> whose message is the CA's own
    /// <c>detail</c> wherever there is one — that sentence ends up on the operator's Routes page, and it
    /// is invariably more useful than anything this client could say about a status code.
    /// </summary>
    private AcmeException Failure(HttpResponseMessage response, string body, string what) {
        AcmeProblem? problem = null;
        if (string.Equals(response.Content.Headers.ContentType?.MediaType, ProblemContentType, StringComparison.OrdinalIgnoreCase))
            problem = Deserialize(body, AcmeJsonContext.Default.AcmeProblem, what);

        var detail = problem?.Detail
            ?? problem?.Subproblems?.FirstOrDefault()?.Detail
            ?? problem?.Type
            ?? (body.Length is > 0 and <= 300 ? body : $"{(int)response.StatusCode} {response.ReasonPhrase}");
        return new AcmeException(problem, response.StatusCode, ParseRetryAfter(response), detail);
    }

    /// <summary>
    /// <c>Retry-After</c> as a duration, whether the CA sent delta-seconds or an HTTP date. A date in the
    /// past becomes zero rather than a negative delay.
    /// </summary>
    private TimeSpan? ParseRetryAfter(HttpResponseMessage response) {
        var header = response.Headers.RetryAfter;
        if (header is null) return null;
        if (header.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header.Date is { } date) {
            // Against the *server's* clock where we have one: a Retry-After date is the CA's statement
            // about its own timeline, and subtracting our clock would fold any skew into the wait.
            var now = response.Headers.Date ?? _time.GetUtcNow();
            var wait = date - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    private T Parse<T>(AcmeResponse response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, string what)
        where T : class =>
        Deserialize(response.Body, typeInfo, what)
        ?? throw new AcmeException(null, response.Status, null, $"The CA returned an empty {what} document.");

    private T? Deserialize<T>(string body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, string what)
        where T : class {
        try {
            return JsonSerializer.Deserialize(body, typeInfo);
        } catch (JsonException ex) {
            _logger.LogDebug(ex, "The ACME response for {What} was not valid JSON.", what);
            return null;
        }
    }

    // ── Transport construction ────────────────────────────────────────────────

    /// <summary>
    /// The <see cref="HttpClient"/> the ACME traffic goes over.
    /// </summary>
    /// <remarks>
    /// Redirects are off: an ACME POST is signed for one exact URL, so following a redirect would resend
    /// a JWS whose <c>url</c> header names somewhere else — which the CA at the far end must reject.
    /// Better to surface the 3xx.
    /// <para>
    /// <paramref name="caBundlePath"/> makes the roots in that file trusted <em>in addition to</em> the
    /// system store rather than instead of it. That is the whole design of the escape hatch: an operator
    /// pointing Watchtower at an internal step-ca must not thereby stop trusting Let's Encrypt, and a
    /// bundle that has gone stale must not silently disable public CA validation.
    /// </para>
    /// </remarks>
    internal static HttpClient CreateAcmeHttpClient(string? caBundlePath, TimeSpan timeout) {
        var handler = new SocketsHttpHandler {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = false,
        };

        var roots = LoadRoots(caBundlePath);
        if (roots is { Count: > 0 })
            handler.SslOptions.RemoteCertificateValidationCallback =
                (_, certificate, chain, errors) =>
                    errors == SslPolicyErrors.None
                    // ONLY a chain error may be rescued by the bundle. The other two flags must stay
                    // fatal: RemoteCertificateNameMismatch means the certificate is not for the host we
                    // dialled, and a bundle that waved that through would let any holder of a
                    // bundle-issued certificate impersonate the CA — turning an "also trust this root"
                    // setting into "stop verifying hostnames". RemoteCertificateNotAvailable means there
                    // is nothing to verify at all.
                    || (errors == SslPolicyErrors.RemoteCertificateChainErrors
                        && VerifyAgainstCustomRoots(certificate, chain, roots));

        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "watchtower-acme/1.0 (+https://github.com/swimmesberger/Watchtower)");
        return client;
    }

    private static X509Certificate2Collection? LoadRoots(string? caBundlePath) {
        if (string.IsNullOrWhiteSpace(caBundlePath)) return null;
        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(caBundlePath.Trim());
        return roots;
    }

    /// <summary>
    /// Rebuilds the presented chain against <paramref name="roots"/> alone. Revocation is off because an
    /// internal CA typically publishes neither a CRL nor an OCSP responder, and a check that cannot
    /// complete would fail every handshake this callback exists to allow.
    /// </summary>
    private static bool VerifyAgainstCustomRoots(
        X509Certificate? certificate, X509Chain? presented, X509Certificate2Collection roots) {
        if (certificate is null) return false;
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // The intermediates the server sent: without them a chain that is perfectly valid would fail for
        // want of an issuer the local machine has never seen.
        if (presented is not null)
            foreach (var element in presented.ChainElements)
                chain.ChainPolicy.ExtraStore.Add(element.Certificate);
        return chain.Build(leaf);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _accountGate.Dispose();
        _http.Dispose();
    }
}
