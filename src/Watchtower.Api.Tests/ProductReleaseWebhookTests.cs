using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Api.Endpoints;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Tests;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The product release webhook (ADR-0026, docs/products/design.md §Release intake) through the real
/// pipeline: the 404 that refuses to be an existence oracle, the bearer check, the accepted-release
/// body, replay, and the per-product throttle.
/// </summary>
/// <remarks>
/// Every payload here names images by digest, so nothing reaches a registry: the resolution rules are
/// covered against a stubbed registry in <c>ReleaseIntakeTests</c>, and what this suite is about is the
/// transport — status codes, headers and the wire shape a CI workflow reads.
/// </remarks>
public sealed class ProductReleaseWebhookTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Commit = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    private const string ApiDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string WorkerDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string Token = ReleaseWebhookTokens.Prefix + "test-token-value";

    private static string Url(int productId) => $"/api/webhooks/products/{productId}/release";

    // ── who may call ─────────────────────────────────────────────────────────

    /// <summary>
    /// The three closed states answer identically. A distinct code for "disabled" would turn the
    /// endpoint into a product-existence oracle for anyone who can reach the URL.
    /// </summary>
    [Fact]
    public async Task TheWebhookIsANotFoundWhenItIsMissing_Disabled_OrHasNoToken() {
        using var factory = new WatchtowerApiFactory();
        var disabled = await SeedProductAsync(factory, "disabled", enabled: false, token: Token);
        var tokenless = await SeedProductAsync(factory, "tokenless", enabled: true, token: null);
        using var client = factory.CreateApiClient();

        var missing = await client.SendAsync(Report(4242, Token), Ct);
        var off = await client.SendAsync(Report(disabled, Token), Ct);
        var noToken = await client.SendAsync(Report(tokenless, Token), Ct);

        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, noToken.StatusCode);
        Assert.Empty(await ReleasesAsync(factory));
    }

    /// <summary>
    /// A wrong bearer is 401, whether it is absent, another scheme, or a value of the right shape that
    /// differs in one character — the last one being what the constant-time comparison is for.
    /// </summary>
    [Fact]
    public async Task TheWebhookRefusesEveryBearerButTheProductsOwn() {
        using var factory = new WatchtowerApiFactory();
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        using var client = factory.CreateApiClient();

        var none = await client.SendAsync(Report(productId, bearer: null), Ct);
        var wrongScheme = await client.SendAsync(Report(productId, Token, scheme: "Token"), Ct);
        var almost = await client.SendAsync(Report(productId, Token[..^1] + "x"), Ct);
        var otherShape = await client.SendAsync(Report(productId, "wtapp_something"), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongScheme.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, almost.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherShape.StatusCode);
        Assert.Empty(await ReleasesAsync(factory));
    }

    // ── the accepted release ─────────────────────────────────────────────────

    /// <summary>
    /// The 201 body a workflow reads. <c>stacksEnqueued</c> is 0 here because the product has no stacks
    /// at all — the fan-out has its own cases below.
    /// </summary>
    [Fact]
    public async Task AnAcceptedReleaseIsCreatedWithTheDocumentedBody() {
        using var factory = new WatchtowerApiFactory();
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        using var client = factory.CreateApiClient();

        var response = await client.SendAsync(
            Report(productId, Token, images: [$"docker.io/acme/worker@{WorkerDigest}", $"docker.io/acme/api@{ApiDigest}"]),
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = body.RootElement;
        Assert.True(root.GetProperty("releaseId").GetInt32() > 0);
        // No version was sent, so it is the short commit.
        Assert.Equal(Commit[..7], root.GetProperty("version").GetString());
        Assert.Equal(Commit, root.GetProperty("commit").GetString());
        Assert.Equal(0, root.GetProperty("stacksEnqueued").GetInt32());

        var images = root.GetProperty("images").EnumerateArray()
            .Select(i => (i.GetProperty("repository").GetString(), i.GetProperty("digest").GetString()))
            .ToList();
        Assert.Equal(
            [("docker.io/acme/api", ApiDigest), ("docker.io/acme/worker", WorkerDigest)],
            images);

        var release = Assert.Single(await ReleasesAsync(factory));
        Assert.Equal(Release.ViaWebhook, release.CreatedVia);
        Assert.Equal("main", release.Branch);
    }

    // ── the fan-out ──────────────────────────────────────────────────────────

    /// <summary>
    /// An accepted release rolls out to the product's latest-tracking, running, <c>OnChange</c> stacks
    /// and reports how many it reached — <c>stacksEnqueued</c> is what the workflow's log line shows.
    /// </summary>
    /// <remarks>
    /// The whole path, through the real endpoint: the mode flip, the commit of the insert, and the
    /// enqueue that follows it. The deploys carry no release id (invariant 3) — the enqueue's trigger is
    /// all this asserts, because what a deploy then resolves is <c>ReleaseDeployTests</c>' subject.
    /// </remarks>
    [Fact]
    public async Task AnAcceptedReleaseIsRolledOutToTheProductsOnChangeStacks() {
        using var factory = new WatchtowerApiFactory();
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        var tracking = await SeedStackAsync(factory, "tracking", productId, AutoDeployMode.OnChange);
        await SeedStackAsync(factory, "manual-only", productId, AutoDeployMode.Off);
        using var client = factory.CreateApiClient();

        var response = await client.SendAsync(Report(productId, Token), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(1, body.RootElement.GetProperty("stacksEnqueued").GetInt32());
        Assert.Equal([(tracking, "release")], factory.DeployQueue.Calls);
    }

    /// <summary>
    /// A replayed report enqueues nothing. This is what makes <c>curl --retry</c> safe to put in a
    /// workflow: a retry after a timeout must not redeploy a fleet.
    /// </summary>
    [Fact]
    public async Task ARepeatedReportEnqueuesNothing() {
        using var factory = new WatchtowerApiFactory();
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        await SeedStackAsync(factory, "tracking", productId, AutoDeployMode.OnChange);
        using var client = factory.CreateApiClient();

        await client.SendAsync(Report(productId, Token), Ct);
        var replay = await client.SendAsync(Report(productId, Token), Ct);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using var body = JsonDocument.Parse(await replay.Content.ReadAsStringAsync(Ct));
        Assert.Equal(0, body.RootElement.GetProperty("stacksEnqueued").GetInt32());
        // One enqueue in total, from the first call.
        Assert.Single(factory.DeployQueue.Calls);
    }

    /// <summary>
    /// A retried <c>curl</c> is answered 200 with the same release and changes nothing — the
    /// fingerprint, not the commit, is what makes it a replay.
    /// </summary>
    [Fact]
    public async Task ARepeatedReportIsAnswered200WithTheSameRelease() {
        using var factory = new WatchtowerApiFactory();
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        using var client = factory.CreateApiClient();

        var first = await client.SendAsync(Report(productId, Token), Ct);
        var again = await client.SendAsync(Report(productId, Token), Ct);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(Ct), await again.Content.ReadAsStringAsync(Ct));
        Assert.Single(await ReleasesAsync(factory));
    }

    /// <summary>
    /// The branch gate, through the transport: a build from another branch is a 400 that names both
    /// branches, so the reader knows which end to change.
    /// </summary>
    [Fact]
    public async Task AReportFromAnotherBranchIsRefused() {
        using var factory = new WatchtowerApiFactory();
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        using var client = factory.CreateApiClient();

        var response = await client.SendAsync(Report(productId, Token, branch: "feature/x"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var message = await MessageAsync(response);
        Assert.Contains("feature/x", message, StringComparison.Ordinal);
        Assert.Contains("main", message, StringComparison.Ordinal);
        Assert.Empty(await ReleasesAsync(factory));
    }

    /// <summary>The same version for a different build is a 409, not a silently relabelled release.</summary>
    [Fact]
    public async Task AVersionReusedByADifferentBuildIsAConflict() {
        using var factory = new WatchtowerApiFactory();
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        using var client = factory.CreateApiClient();

        await client.SendAsync(Report(productId, Token, version: "1.4.0"), Ct);
        var clash = await client.SendAsync(
            Report(productId, Token, version: "1.4.0", images: [$"docker.io/acme/api@{WorkerDigest}"]), Ct);

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Contains("1.4.0", await MessageAsync(clash), StringComparison.Ordinal);
    }

    /// <summary>The two fields the wire contract requires; their absence is the caller's error.</summary>
    [Theory]
    [InlineData("commit")]
    [InlineData("branch")]
    [InlineData("images")]
    public async Task APayloadMissingARequiredFieldIsRefused(string field) {
        using var factory = new WatchtowerApiFactory();
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        using var client = factory.CreateApiClient();

        var payload = new Dictionary<string, object?> {
            ["commit"] = Commit,
            ["branch"] = "main",
            ["images"] = new[] { $"docker.io/acme/api@{ApiDigest}" },
        };
        payload.Remove(field);

        var request = new HttpRequestMessage(HttpMethod.Post, Url(productId)) {
            Content = JsonContent.Create(payload),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await MessageAsync(response), StringComparison.Ordinal);
    }

    /// <summary>Nothing unbounded is read into memory, even from an authenticated caller.</summary>
    [Fact]
    public async Task AnOversizedBodyIsRefusedWithoutBeingParsed() {
        using var factory = new WatchtowerApiFactory();
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        using var client = factory.CreateApiClient();

        var padding = new string('x', ProductReleaseWebhook.MaxBodyBytes);
        var request = new HttpRequestMessage(HttpMethod.Post, Url(productId)) {
            Content = new StringContent(
                $$"""{"commit":"{{Commit}}","branch":"main","notes":"{{padding}}","images":["docker.io/acme/api@{{ApiDigest}}"]}""",
                Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await ReleasesAsync(factory));
    }

    // ── the throttle ─────────────────────────────────────────────────────────

    /// <summary>
    /// The per-product fixed window: once a product has spent its budget its next report is answered
    /// 429, and another product is unaffected — the partition is the product, not the caller.
    /// </summary>
    [Fact]
    public async Task ReportsAreThrottledPerProduct() {
        using var factory = new WatchtowerApiFactory(("Watchtower:ReleaseWebhookRateLimitPerMinute", "2"));
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        var otherId = await SeedProductAsync(factory, "other", enabled: true, token: Token + "-other");
        using var client = factory.CreateApiClient();

        // Two permits: the first records a release, the second is its replay. Both spend a permit.
        Assert.Equal(HttpStatusCode.Created, (await client.SendAsync(Report(productId, Token), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Report(productId, Token), Ct)).StatusCode);

        var throttled = await client.SendAsync(Report(productId, Token), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // A different product has its own window.
        var neighbour = await client.SendAsync(Report(otherId, Token + "-other"), Ct);
        Assert.Equal(HttpStatusCode.Created, neighbour.StatusCode);
    }

    /// <summary>
    /// The pre-authentication backstop: the route is anonymous, so a caller with no token at all is
    /// throttled by address before the product lookup runs — otherwise anyone who can reach the URL
    /// gets an indexed query per request for free.
    /// </summary>
    [Fact]
    public async Task AnUnauthenticatedCallerIsThrottledByAddress() {
        using var factory = new WatchtowerApiFactory(
            ("Watchtower:ReleaseWebhookClientRateLimitPerMinute", "3"));
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        using var client = factory.CreateApiClient();

        // Three refusals fit the window; each is a 401, so nothing but the client limiter can explain
        // what the fourth gets.
        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.SendAsync(Report(productId, "wtrel_wrong"), Ct)).StatusCode);

        var throttled = await client.SendAsync(Report(productId, "wtrel_wrong"), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // It fires ahead of everything, so even the product's own token gets nothing while it holds.
        var authentic = await client.SendAsync(Report(productId, Token), Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, authentic.StatusCode);
        Assert.Empty(await ReleasesAsync(factory));
    }

    /// <summary>
    /// A registry that cannot be reached is answered 503 with a <c>Retry-After</c>, so a workflow using
    /// <c>curl --retry</c> waits instead of hammering — and nothing is recorded, so the retry is a
    /// clean first attempt.
    /// </summary>
    [Fact]
    public async Task AnUnreachableRegistryIsA503WithARetryAfter() {
        using var factory = new WatchtowerApiFactory {
            AdditionalServices = services =>
                services.AddSingleton<IReleaseDigestResolver>(new UnavailableRegistry()),
        };
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        using var client = factory.CreateApiClient();

        // A tag, not a digest: only a tag reaches the resolver.
        var response = await client.SendAsync(
            Report(productId, Token, images: ["docker.io/acme/api:2026.8"]), Ct);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), response.Headers.RetryAfter?.Delta);
        Assert.Empty(await ReleasesAsync(factory));
    }

    /// <summary>
    /// The body cap holds for a chunked request too, where <c>Content-Length</c> says nothing and only
    /// the read loop can stop it.
    /// </summary>
    [Fact]
    public async Task AnOversizedChunkedBodyIsRefused() {
        using var factory = new WatchtowerApiFactory();
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        using var client = factory.CreateApiClient();

        var padding = new string('x', ProductReleaseWebhook.MaxBodyBytes);
        var json =
            $$"""{"commit":"{{Commit}}","branch":"main","notes":"{{padding}}","images":["docker.io/acme/api@{{ApiDigest}}"]}""";
        var request = new HttpRequestMessage(HttpMethod.Post, Url(productId)) {
            // A stream of unknown length is what makes the request chunked, so the server learns the
            // size only by reading it.
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes(json))),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Content.Headers.ContentLength = null;
        request.Headers.TransferEncodingChunked = true;
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");

        var response = await client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(request.Content.Headers.ContentLength);
        Assert.Empty(await ReleasesAsync(factory));
    }

    /// <summary>A registry nothing can reach — the 503 half of the resolver's three-way outcome.</summary>
    private sealed class UnavailableRegistry : IReleaseDigestResolver {
        public Task<ReleaseDigestResult> ResolveAsync(
            string imageReference, string? username, string? password, CancellationToken ct) =>
            Task.FromResult(ReleaseDigestResult.Unavailable);
    }

    /// <summary>A caller without the token cannot spend the product's budget on its behalf.</summary>
    [Fact]
    public async Task AnUnauthenticatedCallerDoesNotConsumeTheProductsBudget() {
        using var factory = new WatchtowerApiFactory(("Watchtower:ReleaseWebhookRateLimitPerMinute", "1"));
        var productId = await SeedProductAsync(factory, "shop", enabled: true, token: Token);
        using var client = factory.CreateApiClient();

        for (var attempt = 0; attempt < 3; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await client.SendAsync(Report(productId, "wtrel_wrong"), Ct)).StatusCode);

        var real = await client.SendAsync(Report(productId, Token), Ct);
        Assert.Equal(HttpStatusCode.Created, real.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static HttpRequestMessage Report(
        int productId, string? bearer, string branch = "main", string? version = null,
        IReadOnlyList<string>? images = null, string scheme = "Bearer") {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(productId)) {
            Content = JsonContent.Create(new {
                commit = Commit,
                branch,
                images = images ?? [$"docker.io/acme/api@{ApiDigest}"],
                version,
            }),
        };
        if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", $"{scheme} {bearer}");
        return request;
    }

    private static async Task<string> MessageAsync(HttpResponseMessage response) {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return body.RootElement.GetProperty("message").GetString()!;
    }

    /// <summary>A stack of an existing product — the fan-out's raw material.</summary>
    private static async Task<int> SeedStackAsync(
        WatchtowerApiFactory factory, string name, int productId, AutoDeployMode autoDeploy) {
        var stackId = 0;
        await factory.WithScopeAsync(async services => {
            var db = services.GetRequiredService<WatchtowerDbContext>();
            var stack = new Stack {
                Name = name,
                ComposeProjectName = name,
                ProductId = productId,
                AutoDeployMode = autoDeploy,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(Ct);
            stackId = stack.Id;
        });
        return stackId;
    }

    private static async Task<int> SeedProductAsync(
        WatchtowerApiFactory factory, string name, bool enabled, string? token) {
        var productId = 0;
        await factory.WithScopeAsync(async services => {
            var db = services.GetRequiredService<WatchtowerDbContext>();
            var product = TestProducts.New(name, $"https://github.com/acme/{name}.git");
            product.ReleaseWebhookEnabled = enabled;
            product.ReleaseWebhookToken = token;
            db.Products.Add(product);
            await db.SaveChangesAsync(Ct);
            productId = product.Id;
        });
        return productId;
    }

    private static async Task<List<Release>> ReleasesAsync(WatchtowerApiFactory factory) {
        var releases = new List<Release>();
        await factory.WithScopeAsync(async services => {
            var db = services.GetRequiredService<WatchtowerDbContext>();
            releases = await db.Releases.AsNoTracking().OrderBy(r => r.Id).ToListAsync(Ct);
        });
        return releases;
    }
}
