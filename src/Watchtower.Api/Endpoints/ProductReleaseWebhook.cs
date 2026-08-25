using System.Globalization;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Api.Endpoints;

/// <summary>
/// The release webhook a product's CI calls after it has pushed its images
/// (<c>POST /api/webhooks/products/{id}/release</c>, ADR-0026, docs/products/design.md §Release intake).
/// Anonymous like the stack deploy webhook next door, and authenticated the same way: its own bearer
/// token, because a CI runner has no browser session.
/// </summary>
/// <remarks>
/// <para>
/// <b>404 is the answer to three different questions</b> — no such product, webhook disabled, no token
/// generated — deliberately: a distinct 403 for "disabled" would turn this endpoint into an
/// existence oracle for every product id, which is exactly what the stack webhook avoids.
/// </para>
/// <para>
/// <b>Everything below the token check is the shared pipeline.</b> Validation, tag→digest resolution,
/// the fingerprint and the write all live in <see cref="ReleaseIntakeService"/>, which
/// <c>products.createRelease</c> runs too. What this endpoint owns is the transport: authentication,
/// the two rate limits (see <see cref="ReleaseWebhookRateLimiter"/>), the body cap, and the mapping
/// from intake's outcome onto the status-code table in the design doc.
/// </para>
/// <para>
/// <b>Nothing is deployed.</b> <c>stacksEnqueued</c> is always 0 in this stage — the field is in the
/// response now so the workflow that reads it does not change shape when stage 4 makes it non-zero.
/// </para>
/// </remarks>
public static class ProductReleaseWebhook {
    /// <summary>The route, in one place so the UI snippet and the tests cannot drift from it.</summary>
    public const string Route = "/api/webhooks/products/{id:int}/release";

    /// <summary>
    /// Largest accepted request body. Twenty image references and a commit fit in a fraction of this;
    /// the cap exists so an anonymous caller with a valid token cannot stream an unbounded body into
    /// memory before anything has looked at it.
    /// </summary>
    public const int MaxBodyBytes = 16 * 1024;

    /// <summary>Seconds a caller is told to wait after a registry timeout — the design's <c>Retry-After: 30</c>.</summary>
    private const int RegistryRetryAfterSeconds = 30;

    /// <summary>What CI sends. Every field is nullable here so a missing one is a 400, not a 500.</summary>
    /// <param name="Commit">Required, 40-hex — <c>${{ github.sha }}</c>.</param>
    /// <param name="Branch">Required — <c>${{ github.ref_name }}</c>; validated against the product's branch.</param>
    /// <param name="Images">Required, 1…20, each <c>repo:tag</c> or <c>repo@sha256:…</c>.</param>
    /// <param name="Version">Optional; defaults to the short commit SHA.</param>
    /// <param name="RunUrl">Optional link back to the CI run.</param>
    /// <param name="Notes">Optional free text.</param>
    public sealed record Request(
        string? Commit,
        string? Branch,
        IReadOnlyList<string>? Images,
        string? Version,
        string? RunUrl,
        string? Notes);

    /// <summary>One image of the accepted release, as the caller sees it after resolution.</summary>
    public sealed record ImageResult(string Repository, string Digest);

    /// <param name="StacksEnqueued">
    /// Always 0 in this stage: releases are recorded, nothing is deployed. Stage 4's
    /// <c>ReleaseRolloutService</c> is what makes this non-zero (design.md §Convergent fan-out).
    /// </param>
    public sealed record Response(
        int ReleaseId,
        string Version,
        string? Commit,
        IReadOnlyList<ImageResult> Images,
        int StacksEnqueued);

    /// <summary>The one body shape every refusal uses, matching the App API's error shape.</summary>
    public sealed record ErrorResponse(string Message);

    /// <summary>Maps the webhook. Called from <see cref="WatchtowerHttpEndpoints"/> beside the deploy one.</summary>
    public static void Map(WebApplication app) =>
        app.MapPost(Route, async (
            int id,
            HttpRequest request,
            WatchtowerDbContext db,
            ReleaseIntakeService intake,
            ReleaseWebhookRateLimiter limiter,
            CancellationToken ct) => {
            // Before anything touches the database: this route is anonymous, so an unauthenticated
            // caller must not be able to spend an indexed lookup per request for free. Generous
            // enough that a whole CI fleet behind one address never meets it (see the options).
            if (!limiter.TryAcquireClient(request.HttpContext)) return TooManyRequests();

            var product = await db.Products.AsNoTracking()
                .Where(p => p.Id == id)
                .Select(p => new { p.ReleaseWebhookEnabled, p.ReleaseWebhookToken })
                .FirstOrDefaultAsync(ct);

            // Missing, disabled, or never given a token — one answer for all three (see the remarks).
            if (product is null || !product.ReleaseWebhookEnabled
                || string.IsNullOrEmpty(product.ReleaseWebhookToken)) {
                return Results.NotFound();
            }

            var presented = ReleaseWebhookTokens.ExtractBearer(request.Headers.Authorization.ToString());
            if (!ReleaseWebhookTokens.Verify(presented, product.ReleaseWebhookToken))
                return Results.Unauthorized();

            // The product's own budget, after the token check on purpose: it protects the expensive
            // half — registry lookups and writes — and spending it required holding the token, so a
            // stranger cannot lock a product's CI out by hammering the URL. The per-client limit above
            // is what bounds the stranger.
            if (!limiter.TryAcquire(id)) return TooManyRequests();

            var body = await ReadBoundedBodyAsync(request, ct);
            if (body is null)
                return BadRequest($"The request body must be JSON of at most {MaxBodyBytes / 1024} KB.");

            Request? payload;
            try {
                payload = JsonSerializer.Deserialize(
                    body, WatchtowerHttpJsonContext.Default.ReleaseWebhookRequest);
            } catch (JsonException) {
                return BadRequest("The request body must be a JSON object.");
            }
            if (payload is null) return BadRequest("The request body must be a JSON object.");

            // The two fields the contract requires the workflow to send. Their *shape* is intake's
            // business (a 40-hex commit, a branch that matches the product) — this only checks that
            // they are there at all, which is a property of the wire format rather than of a release.
            if (string.IsNullOrWhiteSpace(payload.Commit)) return BadRequest("'commit' is required.");
            if (string.IsNullOrWhiteSpace(payload.Branch)) return BadRequest("'branch' is required.");
            if (payload.Images is null || payload.Images.Count == 0)
                return BadRequest("'images' must list at least one image.");

            var result = await intake.PublishAsync(
                new ReleaseIntakeRequest(
                    id,
                    payload.Images,
                    Release.ViaWebhook,
                    CommitSha: payload.Commit,
                    Branch: payload.Branch,
                    Version: payload.Version,
                    RunUrl: payload.RunUrl,
                    Notes: payload.Notes,
                    CallerIp: request.HttpContext.Connection.RemoteIpAddress?.ToString()),
                ct);

            return result.Status switch {
                ReleaseIntakeStatus.Created => Results.Json(Describe(result.Release!),
                    statusCode: StatusCodes.Status201Created),
                // A retried curl: the same release, no fan-out, nothing written a second time.
                ReleaseIntakeStatus.Replayed => Results.Json(Describe(result.Release!)),
                ReleaseIntakeStatus.VersionConflict => Results.Json(new ErrorResponse(result.Error!),
                    statusCode: StatusCodes.Status409Conflict),
                ReleaseIntakeStatus.RegistryUnavailable => RegistryUnavailable(result.Error!),
                // The product was deleted between the lookup above and the write.
                ReleaseIntakeStatus.ProductNotFound => Results.NotFound(),
                _ => BadRequest(result.Error!),
            };
        });

    /// <summary>The accepted release, in the shape the workflow reads.</summary>
    private static Response Describe(Release release) => new(
        release.Id,
        release.Version,
        release.CommitSha,
        [.. release.Images
            .OrderBy(i => i.Repository, StringComparer.Ordinal)
            .Select(i => new ImageResult(i.Repository, i.Digest))],
        // Stage 4 replaces this with the fan-out's count.
        StacksEnqueued: 0);

    /// <summary>
    /// Reads at most <see cref="MaxBodyBytes"/> from the request, or null when the body is larger.
    /// </summary>
    /// <remarks>
    /// Read manually rather than through <c>ReadFromJsonAsync</c> because the cap has to apply to a
    /// chunked body too, where <c>Content-Length</c> says nothing.
    /// </remarks>
    private static async Task<byte[]?> ReadBoundedBodyAsync(HttpRequest request, CancellationToken ct) {
        if (request.ContentLength > MaxBodyBytes) return null;

        using var buffer = new MemoryStream();
        var chunk = new byte[8 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0) {
            if (buffer.Length + read > MaxBodyBytes) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static IResult BadRequest(string message) =>
        Results.Json(new ErrorResponse(message), statusCode: StatusCodes.Status400BadRequest);

    private static IResult TooManyRequests() =>
        Results.Json(
            new ErrorResponse("Too many release reports for this product. Try again in a minute."),
            statusCode: StatusCodes.Status429TooManyRequests);

    /// <summary>
    /// 503 with a <c>Retry-After</c>, so a workflow using <c>curl --retry</c> waits rather than
    /// hammering, and <c>-sSf</c> still fails the job when it does not.
    /// </summary>
    private static IResult RegistryUnavailable(string message) => Results.Json(
        new ErrorResponse(message),
        statusCode: StatusCodes.Status503ServiceUnavailable).WithRetryAfter(RegistryRetryAfterSeconds);

    /// <summary>Adds a <c>Retry-After</c> header to a result without rebuilding it.</summary>
    private static IResult WithRetryAfter(this IResult result, int seconds) =>
        new RetryAfterResult(result, seconds);

    private sealed class RetryAfterResult(IResult inner, int seconds) : IResult {
        public Task ExecuteAsync(HttpContext httpContext) {
            httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            return inner.ExecuteAsync(httpContext);
        }
    }
}

/// <summary>
/// The two fixed windows on the release webhook, both shaped like the login limiter
/// (<see cref="Authentication.LoginRateLimiting"/>): one partitioned by <em>client address</em>, taken
/// before the request touches the database, and one partitioned by <em>product id</em>, taken after the
/// token check.
/// </summary>
/// <remarks>
/// <para>
/// Two, because one cannot do both jobs. Partitioning only by product leaves the anonymous half of the
/// route unbounded — the lookup runs for anyone who can reach the URL. Partitioning only by client lets
/// one runaway workflow spend its own address's budget while telling you nothing about which product is
/// misbehaving, and a CI fleet behind one NAT would share a window. So the client limit is the cheap
/// pre-auth backstop and the product limit is the real budget, and spending the latter requires the
/// token — which is what keeps a stranger from locking a product's CI out.
/// </para>
/// <para>
/// The client partition keys on the <em>connection</em> address rather than <c>X-Forwarded-For</c>, for
/// the reason spelled out on <see cref="Authentication.LoginRateLimiting"/>: Watchtower processes only
/// <c>X-Forwarded-Proto</c>, so trusting the forwarded address here would let a direct caller rotate
/// past the limit at will. Behind a reverse proxy every caller therefore shares the proxy's address,
/// which is why this limit is generous and the product limit is the one that means anything.
/// </para>
/// <para>
/// A service the endpoint asks rather than the ASP.NET rate-limiting middleware, because that
/// middleware is only in the pipeline when central authentication is enabled — and this endpoint has
/// to be throttled in both modes. It also lets the 429 carry this endpoint's own error body instead of
/// the login limiter's.
/// </para>
/// <para>
/// The window options are captured when a partition is first created, so changing a setting takes
/// effect for partitions that have not been seen since — the same "read once" property the deploy
/// concurrency gate has, and for the same reason: a limiter resized underneath its own window would
/// admit more than either value allows.
/// </para>
/// </remarks>
public sealed class ReleaseWebhookRateLimiter : IDisposable {
    /// <summary>
    /// Partition key for a connection that reports no remote address — as with the in-process test
    /// server. All such requests share one bucket, which is the safe (stricter) direction.
    /// </summary>
    private const string UnknownClientKey = "unknown";

    private readonly PartitionedRateLimiter<int> _perProduct;
    private readonly PartitionedRateLimiter<string> _perClient;

    public ReleaseWebhookRateLimiter(IOptionsMonitor<WatchtowerOptions> options) {
        _perProduct = PartitionedRateLimiter.Create<int, int>(productId =>
            RateLimitPartition.GetFixedWindowLimiter(productId, _ => new FixedWindowRateLimiterOptions {
                PermitLimit = options.CurrentValue.ResolveReleaseWebhookRateLimit(),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
        _perClient = PartitionedRateLimiter.Create<string, string>(client =>
            RateLimitPartition.GetFixedWindowLimiter(client, _ => new FixedWindowRateLimiterOptions {
                PermitLimit = options.CurrentValue.ResolveReleaseWebhookClientRateLimit(),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    }

    /// <summary>Takes one permit for <paramref name="productId"/>, or false when the window is full.</summary>
    public bool TryAcquire(int productId) {
        using var lease = _perProduct.AttemptAcquire(productId);
        return lease.IsAcquired;
    }

    /// <summary>Takes one permit for the request's connection address, before anything else runs.</summary>
    public bool TryAcquireClient(HttpContext context) {
        var client = context.Connection.RemoteIpAddress?.ToString() ?? UnknownClientKey;
        using var lease = _perClient.AttemptAcquire(client);
        return lease.IsAcquired;
    }

    public void Dispose() {
        _perProduct.Dispose();
        _perClient.Dispose();
    }
}
