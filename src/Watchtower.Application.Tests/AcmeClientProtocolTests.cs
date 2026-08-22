using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The wire behaviour of <see cref="AcmeClient"/>, against a stub handler that answers whatever a test
/// needs it to. These are the rules RFC 8555 states and a CA enforces silently: nonces are single-use and
/// arrive on failures too, <c>badNonce</c> is retryable and the retry must be <em>re-signed</em>, and the
/// difference between an empty payload and an empty object is the difference between reading a challenge
/// and triggering it.
/// </summary>
public sealed class AcmeClientProtocolTests : IDisposable {
    private const string DirectoryUrl = "https://ca.test/directory";
    private const string AccountUrl = "https://ca.test/acct/1";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "watchtower-acme-client-tests", Guid.NewGuid().ToString("N"));

    private readonly StubCa _ca = new();

    private (AcmeClient Client, AcmeAccountKey Account) NewClient() {
        var account = AcmeAccountKey.Load(_root, DirectoryUrl, NullLogger.Instance);
        var http = new HttpClient(_ca) { BaseAddress = new Uri("https://ca.test/") };
        return (new AcmeClient(http, account, TimeProvider.System, NullLogger<AcmeClient>.Instance), account);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(AcmeClient Client, AcmeAccountKey Account)> RegisteredAsync(string? email = "ops@example.invalid") {
        var pair = NewClient();
        await pair.Client.GetDirectoryAsync(new Uri(DirectoryUrl), Ct);
        await pair.Client.EnsureAccountAsync(email, null, null, Ct);
        return pair;
    }

    // ── Nonces ────────────────────────────────────────────────────────────────

    /// <summary>
    /// One <c>newNonce</c> round trip for the whole run: every subsequent nonce is harvested off the
    /// previous response, which is the difference between one and two requests per ACME operation.
    /// </summary>
    [Fact]
    public async Task NoncesAreHarvestedFromEveryResponse() {
        var (client, account) = await RegisteredAsync();
        using var _ = client;
        using var __ = account;

        await client.NewOrderAsync("app.example.invalid", Ct);
        await client.NewOrderAsync("other.example.invalid", Ct);

        Assert.Equal(1, _ca.Requests.Count(r => r.Method == HttpMethod.Head));
    }

    /// <summary>A failure carries a fresh nonce too (§6.5) — otherwise every error would cost an extra round trip.</summary>
    [Fact]
    public async Task ANonceIsTakenFromAnErrorResponseToo() {
        var (client, account) = await RegisteredAsync();
        using var _ = client;
        using var __ = account;
        _ca.NextOrderProblem = Problem(HttpStatusCode.Forbidden, "urn:ietf:params:acme:error:rejectedIdentifier", "no");

        await Assert.ThrowsAsync<AcmeException>(() => client.NewOrderAsync("a.example.invalid", Ct));
        await client.NewOrderAsync("b.example.invalid", Ct);

        Assert.Equal(1, _ca.Requests.Count(r => r.Method == HttpMethod.Head));
    }

    /// <summary>
    /// The retry is a <em>new signature</em>, not a resend: a JWS is bound to one nonce, so replaying the
    /// bytes would fail with the same <c>badNonce</c> forever.
    /// </summary>
    [Fact]
    public async Task ABadNonce_IsReplayedOnceWithAFreshSignature() {
        var (client, account) = await RegisteredAsync();
        using var _ = client;
        using var __ = account;
        _ca.BadNonceCount = 1;

        var (order, _) = await client.NewOrderAsync("app.example.invalid", Ct);

        Assert.Equal("pending", order.Status);
        var attempts = _ca.Requests.Where(r => r.Path == "/new-order").ToList();
        Assert.Equal(2, attempts.Count);
        Assert.NotEqual(attempts[0].Nonce, attempts[1].Nonce);
        Assert.NotEqual(attempts[0].Body, attempts[1].Body);
    }

    /// <summary>
    /// A <c>badNonce</c> invalidates everything in the pool, not just the nonce that was rejected.
    /// </summary>
    /// <remarks>
    /// The reason it says badNonce is usually that the CA rotated its nonce key or failed over to
    /// another instance, and in both cases every nonce it issued before is equally dead. Keeping the
    /// rest would spend all three retry attempts one stale nonce at a time and surface as a hard failure
    /// for a host that only needed to re-sign once.
    /// </remarks>
    [Fact]
    public async Task ABadNonce_DiscardsTheWholePool_NotJustTheRejectedOne() {
        var (client, account) = await RegisteredAsync();
        using var _ = client;
        using var __ = account;
        // Two spares in the pool, which is what a run of concurrent orders produces.
        _ca.ExtraNoncesOnNextResponse = 2;
        await client.NewOrderAsync("prime.example.invalid", Ct);
        var stale = _ca.ExtraNoncesIssued.ToArray();
        Assert.Equal(2, stale.Length);

        _ca.BadNonceCount = 1;
        await client.NewOrderAsync("app.example.invalid", Ct);

        // The retry re-signed with the nonce the badNonce response carried, and neither of the pooled
        // ones was ever presented — before the retry or after it.
        Assert.DoesNotContain(_ca.Requests, r => r.Nonce is not null && stale.Contains(r.Nonce));
    }

    [Fact]
    public async Task ABadNonceThatNeverClears_Surfaces() {
        var (client, account) = await RegisteredAsync();
        using var _ = client;
        using var __ = account;
        _ca.BadNonceCount = 10;

        var error = await Assert.ThrowsAsync<AcmeException>(() => client.NewOrderAsync("app.example.invalid", Ct));

        Assert.True(error.IsType(AcmeProblemTypes.BadNonce));
        Assert.Equal(3, _ca.Requests.Count(r => r.Path == "/new-order"));
    }

    // ── Problem documents ─────────────────────────────────────────────────────

    /// <summary>
    /// The CA's own sentence is what lands on the operator's Routes page, so it has to survive the trip
    /// out of the client unaltered.
    /// </summary>
    [Fact]
    public async Task AProblemDocumentBecomesTheExceptionMessage() {
        var (client, account) = await RegisteredAsync();
        using var _ = client;
        using var __ = account;
        _ca.NextOrderProblem = Problem(
            HttpStatusCode.Forbidden, AcmeProblemTypes.RejectedIdentifier,
            "Cannot issue for \"app.example.invalid\": Domain is on a blocklist");

        var error = await Assert.ThrowsAsync<AcmeException>(() => client.NewOrderAsync("app.example.invalid", Ct));

        Assert.Equal("Cannot issue for \"app.example.invalid\": Domain is on a blocklist", error.Message);
        Assert.True(error.IsType(AcmeProblemTypes.RejectedIdentifier));
        Assert.Equal(HttpStatusCode.Forbidden, error.Status);
    }

    [Fact]
    public async Task ARetryAfterInSeconds_IsSurfaced() {
        var (client, account) = await RegisteredAsync();
        using var _ = client;
        using var __ = account;
        _ca.NextOrderProblem = Problem(
            HttpStatusCode.TooManyRequests, AcmeProblemTypes.RateLimited, "too many certificates");
        _ca.NextRetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3600));

        var error = await Assert.ThrowsAsync<AcmeException>(() => client.NewOrderAsync("app.example.invalid", Ct));

        Assert.Equal(TimeSpan.FromHours(1), error.RetryAfter);
    }

    /// <summary>The other legal form — an HTTP date, which several CAs prefer for the longer limits.</summary>
    [Fact]
    public async Task ARetryAfterAsAnHttpDate_IsSurfaced() {
        var (client, account) = await RegisteredAsync();
        using var _ = client;
        using var __ = account;
        _ca.ServerDate = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        _ca.NextOrderProblem = Problem(
            HttpStatusCode.TooManyRequests, AcmeProblemTypes.RateLimited, "too many certificates");
        _ca.NextRetryAfter = new RetryConditionHeaderValue(_ca.ServerDate.Value.AddHours(2));

        var error = await Assert.ThrowsAsync<AcmeException>(() => client.NewOrderAsync("app.example.invalid", Ct));

        Assert.Equal(TimeSpan.FromHours(2), error.RetryAfter);
    }

    // ── Payload shapes ────────────────────────────────────────────────────────

    /// <summary>
    /// The single most consequential byte in the client: an empty payload asks the CA to show the
    /// challenge, an empty object asks it to validate. Getting it wrong produces an order that hangs at
    /// <c>pending</c> until it expires, with nothing in any log to say why.
    /// </summary>
    [Fact]
    public async Task TriggeringAChallenge_SendsAnEmptyObject_WhileReadsSendNothing() {
        var (client, account) = await RegisteredAsync();
        using var _ = client;
        using var __ = account;

        await client.GetAuthorizationAsync("https://ca.test/authz/1", Ct);
        await client.TriggerChallengeAsync("https://ca.test/chall/1", Ct);

        Assert.Equal("", _ca.Requests.Single(r => r.Path == "/authz/1").Payload);
        Assert.Equal("{}", _ca.Requests.Single(r => r.Path == "/chall/1").Payload);
    }

    /// <summary>
    /// Byte-exact <c>application/jose+json</c>, with no <c>charset</c> parameter.
    /// </summary>
    /// <remarks>
    /// Boulder — the software behind Let's Encrypt — compares this header against the literal string and
    /// answers 415 <c>malformed</c> to anything else. <see cref="StringContent"/>'s encoding overload
    /// appends <c>; charset=utf-8</c>, which is a perfectly correct header that fails every request
    /// against the CA that matters most, and which no fake CA that is lenient here would ever catch.
    /// </remarks>
    [Fact]
    public async Task EveryRequest_CarriesTheExactJoseContentType() {
        var (client, account) = await RegisteredAsync();
        using var _ = client;
        using var __ = account;

        await client.NewOrderAsync("app.example.invalid", Ct);
        await client.GetAuthorizationAsync("https://ca.test/authz/1", Ct);

        var posts = _ca.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        Assert.NotEmpty(posts);
        foreach (var request in posts)
            Assert.Equal("application/jose+json", request.ContentType);
    }

    [Fact]
    public async Task DownloadingTheCertificate_AsksForThePemChain() {
        var (client, account) = await RegisteredAsync();
        using var _ = client;
        using var __ = account;

        var pem = await client.DownloadCertificateAsync("https://ca.test/cert/1", Ct);

        Assert.Equal(StubCa.CertificatePem, pem);
        var request = _ca.Requests.Single(r => r.Path == "/cert/1");
        Assert.Equal("", request.Payload);
        Assert.Contains("application/pem-certificate-chain", request.Accept);
    }

    // ── Account ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RegistrationUsesTheJwk_AndEverythingAfterwardsUsesTheKid() {
        var (client, account) = await RegisteredAsync();
        using var _ = client;
        using var __ = account;

        await client.NewOrderAsync("app.example.invalid", Ct);

        var registration = _ca.Requests.Single(r => r.Path == "/new-account");
        Assert.True(registration.HasJwk);
        Assert.Null(registration.Kid);

        var order = _ca.Requests.Single(r => r.Path == "/new-order");
        Assert.False(order.HasJwk);
        Assert.Equal(AccountUrl, order.Kid);
        Assert.Equal(AccountUrl, account.AccountUrl);
    }

    [Fact]
    public async Task AContactIsSentOnlyWhenThereIsOne() {
        var (withEmail, account1) = await RegisteredAsync("ops@example.invalid");
        withEmail.Dispose();
        account1.Dispose();
        var payload = JsonDocument.Parse(_ca.Requests.Single(r => r.Path == "/new-account").Payload!).RootElement;
        Assert.Equal("mailto:ops@example.invalid", payload.GetProperty("contact")[0].GetString());
        Assert.True(payload.GetProperty("termsOfServiceAgreed").GetBoolean());

        _ca.Reset();
        Directory.Delete(_root, recursive: true);

        // Blank means the member is absent, not present-and-empty: at least one CA rejects `[]`.
        var (blank, account2) = await RegisteredAsync("   ");
        blank.Dispose();
        account2.Dispose();
        var second = JsonDocument.Parse(_ca.Requests.Single(r => r.Path == "/new-account").Payload!).RootElement;
        Assert.False(second.TryGetProperty("contact", out _));
    }

    [Fact]
    public async Task AnExternalAccountBinding_IsCarriedInTheRegistration() {
        var (client, account) = NewClient();
        using var _ = client;
        using var __ = account;
        await client.GetDirectoryAsync(new Uri(DirectoryUrl), Ct);

        await client.EnsureAccountAsync(null, "kid-9", Base64Url.EncodeToString(new byte[32]), Ct);

        var payload = JsonDocument.Parse(_ca.Requests.Single(r => r.Path == "/new-account").Payload!).RootElement;
        var eab = payload.GetProperty("externalAccountBinding");
        var header = JsonDocument.Parse(
            Base64Url.DecodeFromChars(eab.GetProperty("protected").GetString())).RootElement;
        Assert.Equal("HS256", header.GetProperty("alg").GetString());
        Assert.Equal("kid-9", header.GetProperty("kid").GetString());
    }

    /// <summary>
    /// The CA forgot the account — a CA-side reset, or an account deactivated out of band. The key is
    /// still good, so registering it again is both safe (the same key returns the same account) and the
    /// only way forward.
    /// </summary>
    [Fact]
    public async Task AnUnknownAccount_IsRegisteredAgainOnceAndTheRequestSucceeds() {
        var (client, account) = await RegisteredAsync();
        using var _ = client;
        using var __ = account;
        _ca.ForgetAccountOnce = true;

        var (order, _) = await client.NewOrderAsync("app.example.invalid", Ct);

        Assert.Equal("pending", order.Status);
        Assert.Equal(2, _ca.Requests.Count(r => r.Path == "/new-account"));
        Assert.Equal(2, _ca.Requests.Count(r => r.Path == "/new-order"));
        Assert.Equal(AccountUrl, account.AccountUrl);
    }

    [Fact]
    public async Task AnAlreadyRegisteredKey_CostsNoRequestAtAll() {
        var (first, account1) = await RegisteredAsync();
        first.Dispose();
        account1.Dispose();
        _ca.Reset();

        var (client, account) = NewClient();
        using var _ = client;
        using var __ = account;
        await client.GetDirectoryAsync(new Uri(DirectoryUrl), Ct);
        await client.EnsureAccountAsync("ops@example.invalid", null, null, Ct);

        Assert.DoesNotContain(_ca.Requests, r => r.Path == "/new-account");
    }

    // ── Directory ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheDirectoryIsFetchedOnce() {
        var (client, account) = NewClient();
        using var _ = client;
        using var __ = account;

        await client.GetDirectoryAsync(new Uri(DirectoryUrl), Ct);
        var again = await client.GetDirectoryAsync(new Uri(DirectoryUrl), Ct);

        Assert.Equal("https://ca.test/new-order", again.NewOrder);
        Assert.Equal(1, _ca.Requests.Count(r => r.Path == "/directory"));
    }

    [Fact]
    public async Task ADirectoryMissingItsEndpoints_IsRefused() {
        var (client, account) = NewClient();
        using var _ = client;
        using var __ = account;
        _ca.DirectoryBody = """{"newNonce":"https://ca.test/new-nonce"}""";

        var error = await Assert.ThrowsAsync<AcmeException>(
            () => client.GetDirectoryAsync(new Uri(DirectoryUrl), Ct));

        Assert.Contains("newAccount", error.Message);
    }

    public void Dispose() {
        _ca.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static (HttpStatusCode Status, string Type, string Detail) Problem(
        HttpStatusCode status, string type, string detail) => (status, type, detail);

    // ── The stub ──────────────────────────────────────────────────────────────

    /// <summary>One request as the stub CA saw it, already unpacked from its JWS.</summary>
    private sealed record Recorded(
        HttpMethod Method, string Path, string? Body, string? Payload, string? Nonce, string? Kid,
        bool HasJwk, string Accept, string? ContentType);

    /// <summary>
    /// A CA that answers plausibly and can be made to misbehave in the specific ways the protocol says
    /// the client must survive. Deliberately not a full ACME implementation — the end-to-end suite runs
    /// against one of those; this one exists to produce the responses a real CA only produces under load.
    /// </summary>
    private sealed class StubCa : HttpMessageHandler {
        public const string CertificatePem = "-----BEGIN CERTIFICATE-----\nZmFrZQ==\n-----END CERTIFICATE-----\n";

        private int _nonce;

        /// <summary>Set while answering the directory, whose response carries no nonce.</summary>
        private bool IsDirectory { get; set; }

        public List<Recorded> Requests { get; } = [];
        public string? DirectoryBody { get; set; }
        public int BadNonceCount { get; set; }

        /// <summary>
        /// Extra <c>Replay-Nonce</c> values to put on the next response, so the client's pool holds more
        /// than the one entry a strictly serial run produces.
        /// </summary>
        public int ExtraNoncesOnNextResponse { get; set; }

        /// <summary>The extra nonces handed out, so a test can assert they were never presented back.</summary>
        public List<string> ExtraNoncesIssued { get; } = [];
        public bool ForgetAccountOnce { get; set; }
        public (HttpStatusCode Status, string Type, string Detail)? NextOrderProblem { get; set; }
        public RetryConditionHeaderValue? NextRetryAfter { get; set; }
        public DateTimeOffset? ServerDate { get; set; }

        public void Reset() {
            Requests.Clear();
            NextOrderProblem = null;
            NextRetryAfter = null;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var accept = string.Join(", ", request.Headers.Accept.Select(a => a.MediaType));
            // The header verbatim, parameters and all — the whole point of the assertion below.
            var contentType = request.Content?.Headers.ContentType?.ToString();

            string? nonce = null, kid = null, payload = null;
            var hasJwk = false;
            if (body is not null && body.StartsWith('{')) {
                using var jws = JsonDocument.Parse(body);
                var header = JsonDocument.Parse(
                    Base64Url.DecodeFromChars(jws.RootElement.GetProperty("protected").GetString())).RootElement;
                nonce = header.GetProperty("nonce").GetString();
                kid = header.TryGetProperty("kid", out var k) ? k.GetString() : null;
                hasJwk = header.TryGetProperty("jwk", out _);
                var encoded = jws.RootElement.GetProperty("payload").GetString()!;
                payload = encoded.Length == 0
                    ? ""
                    : Encoding.UTF8.GetString(Base64Url.DecodeFromChars(encoded));
            }
            Requests.Add(
                new Recorded(request.Method, path, body, payload, nonce, kid, hasJwk, accept, contentType));

            if (path == "/directory") {
                IsDirectory = true;
                try {
                    return Respond(HttpStatusCode.OK, DirectoryBody ?? """
                        {
                          "newNonce": "https://ca.test/new-nonce",
                          "newAccount": "https://ca.test/new-account",
                          "newOrder": "https://ca.test/new-order",
                          "meta": { "termsOfService": "https://ca.test/terms" }
                        }
                        """, "application/json");
                } finally {
                    IsDirectory = false;
                }
            }

            if (path == "/new-nonce")
                return Respond(HttpStatusCode.OK, "", "application/json");

            if (BadNonceCount > 0 && path != "/new-account") {
                BadNonceCount--;
                return Fail(HttpStatusCode.BadRequest, AcmeProblemTypes.BadNonce, "JWS has an invalid anti-replay nonce");
            }

            switch (path) {
                case "/new-account": {
                    var response = Respond(
                        HttpStatusCode.Created, """{"status":"valid"}""", "application/json");
                    response.Headers.Location = new Uri(AccountUrl);
                    return response;
                }
                case "/new-order": {
                    if (ForgetAccountOnce) {
                        ForgetAccountOnce = false;
                        return Fail(
                            HttpStatusCode.BadRequest, AcmeProblemTypes.AccountDoesNotExist,
                            "No account exists with the provided key");
                    }
                    if (NextOrderProblem is { } problem) {
                        NextOrderProblem = null;
                        return Fail(problem.Status, problem.Type, problem.Detail);
                    }
                    var response = Respond(HttpStatusCode.Created, """
                        {
                          "status": "pending",
                          "identifiers": [{ "type": "dns", "value": "app.example.invalid" }],
                          "authorizations": ["https://ca.test/authz/1"],
                          "finalize": "https://ca.test/finalize/1"
                        }
                        """, "application/json");
                    response.Headers.Location = new Uri("https://ca.test/order/1");
                    return response;
                }
                case "/authz/1":
                    return Respond(HttpStatusCode.OK, """
                        {
                          "identifier": { "type": "dns", "value": "app.example.invalid" },
                          "status": "pending",
                          "challenges": [
                            { "type": "http-01", "url": "https://ca.test/chall/1", "status": "pending", "token": "tok" }
                          ]
                        }
                        """, "application/json");
                case "/chall/1":
                    return Respond(HttpStatusCode.OK, """
                        { "type": "http-01", "url": "https://ca.test/chall/1", "status": "processing", "token": "tok" }
                        """, "application/json");
                case "/cert/1":
                    return Respond(HttpStatusCode.OK, CertificatePem, "application/pem-certificate-chain");
                default:
                    return Fail(HttpStatusCode.NotFound, AcmeProblemTypes.Malformed, $"no resource at {path}");
            }
        }

        private HttpResponseMessage Respond(HttpStatusCode status, string body, string contentType) {
            var response = new HttpResponseMessage(status) {
                Content = new StringContent(body, Encoding.UTF8, contentType),
            };
            // Every response to a POST or a newNonce carries one, success or failure — that is what makes
            // the pool work. The directory GET does not, matching Let's Encrypt.
            if (!IsDirectory) response.Headers.Add("Replay-Nonce", $"nonce-{Interlocked.Increment(ref _nonce)}");
            for (; ExtraNoncesOnNextResponse > 0; ExtraNoncesOnNextResponse--) {
                var extra = $"nonce-{Interlocked.Increment(ref _nonce)}";
                ExtraNoncesIssued.Add(extra);
                response.Headers.Add("Replay-Nonce", extra);
            }
            if (ServerDate is { } date) response.Headers.Date = date;
            return response;
        }

        private HttpResponseMessage Fail(HttpStatusCode status, string type, string detail) {
            var response = Respond(
                status,
                JsonSerializer.Serialize(new AcmeProblem { Type = type, Detail = detail, Status = (int)status },
                    AcmeJsonContext.Default.AcmeProblem),
                "application/problem+json");
            if (NextRetryAfter is { } retryAfter) {
                response.Headers.RetryAfter = retryAfter;
                NextRetryAfter = null;
            }
            return response;
        }
    }
}
