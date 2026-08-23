using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Services.Acme;

namespace Watchtower.Api.Tests;

/// <summary>
/// An RFC 8555 certificate authority, in process. It speaks the real protocol — JWS-signed requests over
/// single-use nonces, an order state machine, an HTTP-01 challenge it actually fetches, and a CSR it
/// actually signs — against a throwaway root, and can be told to fail in the specific ways a real CA does.
/// </summary>
/// <remarks>
/// This is what makes the issuance path testable end to end without a network. Two decisions carry the
/// design. First, it <em>verifies</em> every signature rather than trusting the client: a JWS with the
/// wrong signing format, both <c>jwk</c> and <c>kid</c>, or a replayed nonce is rejected here exactly as
/// Let's Encrypt would reject it, so a client bug fails the test instead of passing quietly. Second, the
/// HTTP-01 validation is a real request through Watchtower's own <see cref="HttpMessageHandler"/>
/// (<c>TestServer.CreateHandler()</c>), so the middleware order, the host dispatch and the challenge
/// responder are all in the loop — which is where the interesting failures live.
/// </remarks>
public sealed class FakeAcmeServer : IAsyncDisposable {
    private readonly ConcurrentDictionary<string, byte> _nonces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ECDsa> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, OrderState> _orders = new(StringComparer.Ordinal);
    private readonly List<string> _requests = [];
    private readonly X509Certificate2 _root;
    private readonly X509Certificate2 _intermediate;
    private readonly ECDsa _intermediateKey;

    private IHost? _host;
    private int _nextId;
    private int _accountCount;

    public FakeAcmeServer() {
        var from = DateTimeOffset.UtcNow.AddYears(-1);
        var to = DateTimeOffset.UtcNow.AddYears(5);

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=Fake ACME Root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        _root = rootRequest.CreateSelfSigned(from, to);

        _intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest(
            "CN=Fake ACME Intermediate", _intermediateKey, HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        _intermediate = intermediateRequest.Create(_root, from, to, RandomNumberGenerator.GetBytes(12));
    }

    // ── Knobs ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The transport Watchtower reaches this CA through. Handed to the API factory so nothing binds a
    /// socket and no test depends on a free port.
    /// </summary>
    public IAcmeTransportFactory Transport => new Factory(this);

    /// <summary>
    /// How the CA reaches Watchtower to validate a challenge. Set to the API host's own handler after
    /// both are up — the dependency runs both ways, which is exactly the loop being tested.
    /// </summary>
    public HttpMessageHandler? ChallengeTransport { get; set; }

    /// <summary>The directory URL to point <c>Proxy:Yarp:AcmeDirectoryUrl</c> at.</summary>
    public string DirectoryUrl => $"{BaseAddress}directory";

    /// <summary>Every request path the CA received, in order — for asserting that it received none.</summary>
    public IReadOnlyList<string> Requests {
        get { lock (_requests) return [.. _requests]; }
    }

    /// <summary>
    /// Forgets what has been asked so far, so a test can arrange through the CA and then assert that a
    /// later pass reached it not at all — which is the shape of every "this instance must not issue" claim.
    /// </summary>
    public void ForgetRequests() {
        lock (_requests) _requests.Clear();
    }

    /// <summary>Whether the CA has been asked to open an order since the last <see cref="ForgetRequests"/>.</summary>
    public bool SawAnOrder =>
        Requests.Any(path => path.Contains("new-order", StringComparison.Ordinal));

    /// <summary>Refuse the next authorization with this problem instead of validating it.</summary>
    public (string Type, string Detail)? FailValidationWith { get; set; }

    /// <summary>
    /// Answer everything with a bare 500 — a CA that is down, which is a transport failure rather than
    /// anything the operator can act on.
    /// </summary>
    public bool Offline { get; set; }

    /// <summary>Refuse the next order with <c>rateLimited</c> and the given <c>Retry-After</c>.</summary>
    public TimeSpan? RateLimitOrdersFor { get; set; }

    /// <summary>
    /// Create orders already in <c>ready</c>, as a CA does when it is reusing a still-valid
    /// authorization — the ordinary renewal path, and one that must skip the challenge entirely.
    /// </summary>
    public bool OrdersStartReady { get; set; }

    /// <summary>The validity window issued certificates carry. Moved to exercise the renewal window.</summary>
    public TimeSpan CertificateAge { get; set; } = TimeSpan.Zero;

    /// <summary>How long an issued certificate lasts from its <c>notBefore</c>.</summary>
    public TimeSpan CertificateLifetime { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How many accounts have been registered — one, for a correctly behaving client.</summary>
    public int AccountRegistrations => _accountCount;

    /// <summary>How many times a challenge was actually triggered for validation.</summary>
    public int ChallengesTriggered { get; private set; }

    /// <summary>The root, for a test that wants to check what signed the leaf.</summary>
    public X509Certificate2 Root => _root;

    private string BaseAddress { get; set; } = "";

    // ── Lifetime ──────────────────────────────────────────────────────────────

    public async Task StartAsync() {
        _host = await new HostBuilder()
            .ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Warning))
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.AddRouting())
                .Configure(app => {
                    app.UseRouting();
                    app.UseEndpoints(Map);
                }))
            .StartAsync();
        BaseAddress = _host.GetTestServer().BaseAddress.ToString();
    }

    private HttpClient CreateClient() {
        var server = (_host ?? throw new InvalidOperationException("StartAsync the fake CA first."))
            .GetTestServer();
        return new HttpClient(server.CreateHandler()) { BaseAddress = server.BaseAddress };
    }

    public async ValueTask DisposeAsync() {
        if (_host is not null) {
            await _host.StopAsync();
            _host.Dispose();
        }
        foreach (var key in _accounts.Values) key.Dispose();
        _intermediateKey.Dispose();
        _intermediate.Dispose();
        _root.Dispose();
    }

    // ── The protocol ──────────────────────────────────────────────────────────

    private void Map(IEndpointRouteBuilder endpoints) {
        endpoints.MapGet("/directory", (HttpContext http) => {
            Record(http);
            if (Offline) {
                http.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                return Task.CompletedTask;
            }
            return Json(http, new JsonObject {
                ["newNonce"] = $"{BaseAddress}new-nonce",
                ["newAccount"] = $"{BaseAddress}new-account",
                ["newOrder"] = $"{BaseAddress}new-order",
                ["meta"] = new JsonObject { ["termsOfService"] = $"{BaseAddress}terms" },
            });
        });

        // Both verbs: §7.2 defines HEAD, and clients are allowed to use GET.
        endpoints.MapMethods("/new-nonce", ["GET", "HEAD"], (HttpContext http) => {
            Record(http);
            IssueNonce(http);
            http.Response.StatusCode = (int)HttpStatusCode.OK;
            return Task.CompletedTask;
        });

        endpoints.MapPost("/new-account", async (HttpContext http) => {
            Record(http);
            var jws = await VerifyAsync(http, expectJwk: true);
            if (jws is null) return;

            // The same key registering twice gets the same account back (§7.3), which is what makes
            // registration idempotent and a client's re-registration harmless.
            var thumbprint = Thumbprint(jws.Key);
            var existing = _accounts.FirstOrDefault(a => Thumbprint(a.Value) == thumbprint);
            var url = existing.Key;
            if (url is null) {
                url = $"{BaseAddress}acct/{Interlocked.Increment(ref _nextId)}";
                var stored = ECDsa.Create();
                stored.ImportParameters(jws.Key.ExportParameters(false));
                _accounts[url] = stored;
            }
            Interlocked.Increment(ref _accountCount);

            http.Response.Headers.Location = url;
            await Json(http, new JsonObject { ["status"] = "valid" }, HttpStatusCode.Created);
        });

        endpoints.MapPost("/new-order", async (HttpContext http) => {
            Record(http);
            var jws = await VerifyAsync(http, expectJwk: false);
            if (jws is null) return;

            if (RateLimitOrdersFor is { } retryAfter) {
                http.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString();
                await Problem(
                    http, HttpStatusCode.TooManyRequests, AcmeProblemTypes.RateLimited,
                    "too many certificates already issued for this domain");
                return;
            }

            var payload = JsonNode.Parse(jws.Payload)!;
            var domain = payload["identifiers"]![0]!["value"]!.GetValue<string>();
            var id = Interlocked.Increment(ref _nextId).ToString();
            _orders[id] = new OrderState(domain, jws.AccountUrl!) {
                Status = OrdersStartReady ? "ready" : "pending",
                Token = Guid.NewGuid().ToString("N"),
            };

            http.Response.Headers.Location = $"{BaseAddress}order/{id}";
            await Json(http, OrderJson(id, _orders[id]), HttpStatusCode.Created);
        });

        endpoints.MapPost("/order/{id}", async (HttpContext http, string id) => {
            Record(http);
            if (await VerifyAsync(http, expectJwk: false) is null) return;
            if (!_orders.TryGetValue(id, out var order)) {
                await Problem(http, HttpStatusCode.NotFound, AcmeProblemTypes.Malformed, "no such order");
                return;
            }
            await Json(http, OrderJson(id, order));
        });

        endpoints.MapPost("/authz/{id}", async (HttpContext http, string id) => {
            Record(http);
            if (await VerifyAsync(http, expectJwk: false) is null) return;
            if (!_orders.TryGetValue(id, out var order)) {
                await Problem(http, HttpStatusCode.NotFound, AcmeProblemTypes.Malformed, "no such authorization");
                return;
            }
            await Json(http, AuthorizationJson(id, order));
        });

        endpoints.MapPost("/chall/{id}", async (HttpContext http, string id) => {
            Record(http);
            var jws = await VerifyAsync(http, expectJwk: false);
            if (jws is null) return;
            if (!_orders.TryGetValue(id, out var order)) {
                await Problem(http, HttpStatusCode.NotFound, AcmeProblemTypes.Malformed, "no such challenge");
                return;
            }
            // §7.5.1: triggering is a POST with an empty JSON *object*. An empty payload is a read, and a
            // CA that received one would never schedule validation — so the distinction is enforced here.
            if (jws.Payload != "{}") {
                await Problem(
                    http, HttpStatusCode.BadRequest, AcmeProblemTypes.Malformed,
                    "a challenge is triggered with an empty JSON object, not POST-as-GET");
                return;
            }

            ChallengesTriggered++;
            await ValidateAsync(order, http.RequestAborted);
            await Json(http, ChallengeJson(id, order));
        });

        endpoints.MapPost("/finalize/{id}", async (HttpContext http, string id) => {
            Record(http);
            var jws = await VerifyAsync(http, expectJwk: false);
            if (jws is null) return;
            if (!_orders.TryGetValue(id, out var order)) {
                await Problem(http, HttpStatusCode.NotFound, AcmeProblemTypes.Malformed, "no such order");
                return;
            }
            if (order.Status != "ready") {
                await Problem(
                    http, HttpStatusCode.Forbidden, AcmeProblemTypes.Malformed,
                    $"order is not ready to be finalized (status {order.Status})");
                return;
            }

            var csr = Base64Url.DecodeFromChars(JsonNode.Parse(jws.Payload)!["csr"]!.GetValue<string>());
            order.CertificatePem = Sign(csr, order.Domain);
            order.Status = "valid";
            await Json(http, OrderJson(id, order));
        });

        endpoints.MapPost("/cert/{id}", async (HttpContext http, string id) => {
            Record(http);
            if (await VerifyAsync(http, expectJwk: false) is null) return;
            if (!_orders.TryGetValue(id, out var order) || order.CertificatePem is null) {
                await Problem(http, HttpStatusCode.NotFound, AcmeProblemTypes.Malformed, "no certificate");
                return;
            }
            IssueNonce(http);
            http.Response.ContentType = "application/pem-certificate-chain";
            await http.Response.WriteAsync(order.CertificatePem, http.RequestAborted);
        });
    }

    /// <summary>
    /// Fetches <c>/.well-known/acme-challenge/{token}</c> from Watchtower over plain HTTP, on the domain
    /// being validated — the same request Let's Encrypt makes, through the same middleware pipeline.
    /// </summary>
    private async Task ValidateAsync(OrderState order, CancellationToken ct) {
        if (FailValidationWith is { } forced) {
            FailValidationWith = null;
            order.Status = "invalid";
            // The authorization has to move too. A CA that left it pending would have the client poll
            // until its budget ran out, which is a timeout rather than the refusal being modelled.
            order.AuthorizationStatus = "invalid";
            order.Error = forced;
            return;
        }

        if (ChallengeTransport is null)
            throw new InvalidOperationException(
                "The fake CA was asked to validate a challenge with no transport back to Watchtower. "
                + "Set ChallengeTransport once the API host is up.");

        var expected = order.Token + "." + Thumbprint(_accounts[order.AccountUrl]);
        try {
            using var client = new HttpClient(ChallengeTransport, disposeHandler: false);
            var body = await client.GetStringAsync(
                $"http://{order.Domain}/.well-known/acme-challenge/{order.Token}", ct);
            if (body == expected) {
                order.Status = "ready";
                order.AuthorizationStatus = "valid";
                return;
            }
            order.Status = "invalid";
            order.AuthorizationStatus = "invalid";
            order.Error = (AcmeProblemTypes.Unauthorized,
                $"The key authorization file from {order.Domain} did not match the expected value");
        } catch (Exception ex) {
            order.Status = "invalid";
            order.AuthorizationStatus = "invalid";
            order.Error = (AcmeProblemTypes.Connection,
                $"{order.Domain}: Fetching the challenge failed: {ex.Message}");
        }
    }

    /// <summary>Signs the submitted CSR under the intermediate, and returns leaf + intermediate as PEM.</summary>
    private string Sign(byte[] csrDer, string domain) {
        var request = CertificateRequest.LoadSigningRequest(
            csrDer, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);

        // Every issued certificate carries the SAN the client asked for, which is what the certificate
        // store and the SNI callback key on. A CA would check it names the validated identifier.
        var san = request.CertificateExtensions.FirstOrDefault(e => e.Oid?.Value == "2.5.29.17");
        if (san is null) throw new InvalidOperationException("The CSR carries no subject alternative name.");
        var names = new X509SubjectAlternativeNameExtension(san.RawData, san.Critical).EnumerateDnsNames().ToArray();
        if (!names.Contains(domain))
            throw new InvalidOperationException($"The CSR does not name {domain} (it names {string.Join(", ", names)}).");

        var notBefore = DateTimeOffset.UtcNow - CertificateAge;
        var leaf = request.Create(
            _intermediate.SubjectName,
            X509SignatureGenerator.CreateForECDsa(_intermediateKey),
            notBefore,
            notBefore + CertificateLifetime,
            RandomNumberGenerator.GetBytes(12));
        using (leaf) return leaf.ExportCertificatePem() + "\n" + _intermediate.ExportCertificatePem() + "\n";
    }

    // ── JWS verification ──────────────────────────────────────────────────────

    private sealed record VerifiedJws(ECDsa Key, string Payload, string? AccountUrl);

    /// <summary>
    /// Verifies one request's JWS and consumes its nonce, answering with the CA's own problem document
    /// on any failure. Returns null when it answered, which every endpoint checks.
    /// </summary>
    private async Task<VerifiedJws?> VerifyAsync(HttpContext http, bool expectJwk) {
        IssueNonce(http);

        // Byte-exact, exactly as Boulder compares it — no charset parameter, no whitespace variations.
        // Being lenient here is what let a client that sends "application/jose+json; charset=utf-8" pass
        // every test and then fail every request against Let's Encrypt with a 415.
        if (http.Request.ContentType != "application/jose+json") {
            await Problem(
                http, HttpStatusCode.UnsupportedMediaType, AcmeProblemTypes.Malformed,
                $"expected application/jose+json, got {http.Request.ContentType}");
            return null;
        }

        using var reader = new StreamReader(http.Request.Body);
        var body = await reader.ReadToEndAsync(http.RequestAborted);
        using var jws = JsonDocument.Parse(body);
        var protectedHeader = jws.RootElement.GetProperty("protected").GetString()!;
        var payloadEncoded = jws.RootElement.GetProperty("payload").GetString()!;
        var signature = Base64Url.DecodeFromChars(jws.RootElement.GetProperty("signature").GetString());

        using var headerDocument = JsonDocument.Parse(Base64Url.DecodeFromChars(protectedHeader));
        var header = headerDocument.RootElement;

        if (header.GetProperty("alg").GetString() != "ES256") {
            await Problem(http, HttpStatusCode.BadRequest, AcmeProblemTypes.Malformed, "alg must be ES256");
            return null;
        }

        var url = header.GetProperty("url").GetString();
        var requestUrl = $"{http.Request.Scheme}://{http.Request.Host}{http.Request.Path}";
        if (url != requestUrl) {
            // §6.4: the url header binds the signature to one endpoint, which is what stops a signed
            // request being replayed against a different one.
            await Problem(
                http, HttpStatusCode.BadRequest, AcmeProblemTypes.Malformed,
                $"the url header says {url} but this is {requestUrl}");
            return null;
        }

        var nonce = header.GetProperty("nonce").GetString()!;
        if (!_nonces.TryRemove(nonce, out _)) {
            await Problem(
                http, HttpStatusCode.BadRequest, AcmeProblemTypes.BadNonce,
                "JWS has an invalid anti-replay nonce");
            return null;
        }

        var hasJwk = header.TryGetProperty("jwk", out var jwkElement);
        var hasKid = header.TryGetProperty("kid", out var kidElement);
        if (hasJwk == hasKid) {
            await Problem(
                http, HttpStatusCode.BadRequest, AcmeProblemTypes.Malformed,
                "a JWS must carry exactly one of jwk and kid");
            return null;
        }
        if (hasJwk != expectJwk) {
            await Problem(
                http, HttpStatusCode.BadRequest, AcmeProblemTypes.Malformed,
                expectJwk ? "newAccount must be signed with jwk" : "this resource must be signed with kid");
            return null;
        }

        ECDsa key;
        string? accountUrl = null;
        if (hasJwk) {
            key = FromJwk(jwkElement);
        } else {
            accountUrl = kidElement.GetString();
            if (accountUrl is null || !_accounts.TryGetValue(accountUrl, out var stored)) {
                await Problem(
                    http, HttpStatusCode.BadRequest, AcmeProblemTypes.AccountDoesNotExist,
                    "No account exists with the provided key");
                return null;
            }
            key = stored;
        }

        var verified = key.VerifyData(
            Encoding.ASCII.GetBytes($"{protectedHeader}.{payloadEncoded}"),
            signature,
            HashAlgorithmName.SHA256,
            // The format the RFC requires. A client that produced DER would fail here, which is precisely
            // how a real CA reports it — as a bad signature, with no hint about why.
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        if (!verified) {
            await Problem(http, HttpStatusCode.Unauthorized, AcmeProblemTypes.Malformed, "JWS verification failed");
            return null;
        }

        var payload = payloadEncoded.Length == 0
            ? ""
            : Encoding.UTF8.GetString(Base64Url.DecodeFromChars(payloadEncoded));
        return new VerifiedJws(key, payload, accountUrl);
    }

    private static ECDsa FromJwk(JsonElement jwk) {
        var key = ECDsa.Create();
        key.ImportParameters(new ECParameters {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint {
                X = Base64Url.DecodeFromChars(jwk.GetProperty("x").GetString()),
                Y = Base64Url.DecodeFromChars(jwk.GetProperty("y").GetString()),
            },
        });
        return key;
    }

    private static string Thumbprint(ECDsa key) {
        var q = key.ExportParameters(false).Q;
        var json =
            $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url.EncodeToString(q.X!)}\","
            + $"\"y\":\"{Base64Url.EncodeToString(q.Y!)}\"}}";
        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private JsonObject OrderJson(string id, OrderState order) {
        var json = new JsonObject {
            ["status"] = order.Status,
            ["identifiers"] = new JsonArray(
                new JsonObject { ["type"] = "dns", ["value"] = order.Domain }),
            ["authorizations"] = new JsonArray($"{BaseAddress}authz/{id}"),
            ["finalize"] = $"{BaseAddress}finalize/{id}",
        };
        if (order.CertificatePem is not null) json["certificate"] = $"{BaseAddress}cert/{id}";
        if (order.Error is { } error) json["error"] = ProblemJson(error.Type, error.Detail);
        return json;
    }

    private JsonObject AuthorizationJson(string id, OrderState order) => new() {
        ["identifier"] = new JsonObject { ["type"] = "dns", ["value"] = order.Domain },
        ["status"] = order.AuthorizationStatus,
        ["challenges"] = new JsonArray(ChallengeJson(id, order)),
    };

    private JsonObject ChallengeJson(string id, OrderState order) {
        var json = new JsonObject {
            ["type"] = "http-01",
            ["url"] = $"{BaseAddress}chall/{id}",
            ["status"] = order.AuthorizationStatus,
            ["token"] = order.Token,
        };
        if (order.Error is { } error) json["error"] = ProblemJson(error.Type, error.Detail);
        return json;
    }

    private static JsonObject ProblemJson(string type, string detail) =>
        new() { ["type"] = type, ["detail"] = detail };

    private Task Json(HttpContext http, JsonNode body, HttpStatusCode status = HttpStatusCode.OK) {
        IssueNonce(http);
        http.Response.StatusCode = (int)status;
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync(body.ToJsonString(), http.RequestAborted);
    }

    private Task Problem(HttpContext http, HttpStatusCode status, string type, string detail) {
        IssueNonce(http);
        http.Response.StatusCode = (int)status;
        http.Response.ContentType = "application/problem+json";
        var body = ProblemJson(type, detail);
        body["status"] = (int)status;
        return http.Response.WriteAsync(body.ToJsonString(), http.RequestAborted);
    }

    /// <summary>
    /// Every response gets a fresh nonce, success or failure — §6.5, and the property the client's
    /// nonce pool depends on. Guarded because several paths issue then render.
    /// </summary>
    private void IssueNonce(HttpContext http) {
        if (http.Response.Headers.ContainsKey("Replay-Nonce")) return;
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _nonces[nonce] = 0;
        http.Response.Headers["Replay-Nonce"] = nonce;
    }

    private void Record(HttpContext http) {
        lock (_requests) _requests.Add(http.Request.Path.Value ?? "");
    }

    private sealed class OrderState(string domain, string accountUrl) {
        public string Domain { get; } = domain;
        public string AccountUrl { get; } = accountUrl;
        public string Status { get; set; } = "pending";
        public string AuthorizationStatus { get; set; } = "pending";
        public string Token { get; init; } = "";
        public string? CertificatePem { get; set; }
        public (string Type, string Detail)? Error { get; set; }
    }

    private sealed class Factory(FakeAcmeServer server) : IAcmeTransportFactory {
        public HttpClient Create(string? caBundlePath, TimeSpan timeout) => server.CreateClient();
    }
}
