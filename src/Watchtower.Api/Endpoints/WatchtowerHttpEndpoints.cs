using System.IO.Compression;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Api.Authentication;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Api.Endpoints;

/// <summary>
/// Plain HTTP endpoints that don't fit the JSON-RPC model: the externally-facing deploy webhook
/// (bearer auth) and the two Server-Sent-Event streams (deploy output + container logs).
/// </summary>
public static class WatchtowerHttpEndpoints {
    /// <summary>Response body returned by the deploy webhook (202 Accepted).</summary>
    public sealed record WebhookDeployResult(int DeployEventId, string Status);

    /// <summary>
    /// Maps the non-RPC surfaces: the deploy webhook, the two SSE streams, the volume archive download,
    /// the proxy TLS gate, the public App API, and <c>/health</c>. With <paramref name="authEnabled"/> the
    /// two SSE streams and the volume download — which carry deploy output, container logs and raw volume
    /// data, i.e. exactly the data the JSON-RPC handlers now protect — require a login session too;
    /// <c>EventSource</c> and same-origin navigation send cookies, so the UI is unaffected.
    /// </summary>
    /// <remarks>
    /// Endpoints that stay open by design (design.md §11): <c>/health</c> is a liveness probe with no data,
    /// the deploy webhook authenticates callers with its own per-stack bearer token (a CI runner has no
    /// browser session), <c>/api/proxy/ask</c> is Caddy's on-demand-TLS gate — which is reachable through
    /// the proxy and answers 404 there, see <see cref="MapProxyAsk"/> — and the App API and management API
    /// carry their own per-stack token auth (see AppApiEndpoints and MgmtApiEndpoints).
    /// </remarks>
    public static WebApplication MapWatchtowerHttpEndpoints(this WebApplication app, bool authEnabled) {
        MapWebhook(app);
        Protect(MapDeployOutputStream(app), authEnabled);
        Protect(MapContainerLogStream(app), authEnabled);
        Protect(MapVolumeDownload(app), authEnabled);
        MapProxyAsk(app);
        // Public, token-authenticated surface for deployed applications (see AppApiEndpoints). The flag is
        // passed down for its one identity-dependent endpoint: the tenant switcher needs a forwarded
        // assertion, which does not exist with central auth off, so that route answers 404 there.
        app.MapAppApiEndpoints(authEnabled);
        // Public, token-authenticated surface for a stack that manages a template's tenants. Same
        // credential as the App API, plus an operator-managed grant (see MgmtApiEndpoints), and the same
        // identity-dependent tenant listing behind the same flag.
        app.MapMgmtApiEndpoints(authEnabled);
        app.MapGet("/health", () => Results.Ok("healthy"));
        return app;
    }

    /// <summary>
    /// Requires a signed-in <em>operator-realm</em> account on an endpoint, but only when authentication is
    /// configured.
    /// </summary>
    /// <remarks>
    /// The realm half is not optional (docs/central-auth/design.md §13). These are minimal-API endpoints,
    /// so <see cref="Application.Services.SystemRealmAuthorizer"/> — which decorates Elarion's handler
    /// pipeline — never sees them: a bare <c>RequireAuthorization()</c> would accept <em>any</em>
    /// authenticated principal, and a customer realm's account holding a valid <c>__wt_sso</c> on its own
    /// login host would be able to stream deploy output and any container's logs. The policy is the same
    /// rule the handler surface applies, read off the principal instead of the <c>ICurrentUser</c>
    /// snapshot.
    /// <para>
    /// Deliberately <em>not</em> also an Admin-role requirement: a non-administrator operator account could
    /// watch these streams before realms existed, and this is a realm boundary rather than a re-grading of
    /// who may see deploy output.
    /// </para>
    /// </remarks>
    private static void Protect(RouteHandlerBuilder route, bool authEnabled) {
        if (authEnabled) route.RequireAuthorization(WatchtowerSessionDefaults.SystemRealmPolicy);
    }

    /// <summary>
    /// On-demand TLS gate for Caddy (custom domains). Caddy calls this before issuing a certificate for
    /// a requested host; we return 200 only for domains that exist in the route table, so a stray domain
    /// pointed at this host can never trigger unbounded certificate issuance.
    /// </summary>
    /// <remarks>
    /// That answer <em>is</em> a route-existence oracle, so who gets to ask matters. This endpoint is not
    /// reachable only on the internal control network, whatever an earlier version of this comment claimed:
    /// the Watchtower routes (ADR-0023 — every hostname whose route targets this instance) are unprotected
    /// sites that proxy <em>all</em> paths to this app, so anyone who can reach any login page can reach
    /// this path too.
    /// <para>
    /// What separates the one legitimate caller from everyone else is the hop, not the network. Caddy's
    /// on-demand-TLS module calls the <c>ask</c> URL from its TLS machinery, directly, and stamps no
    /// <c>X-Forwarded-*</c> headers on it; anything relayed by a <c>reverse_proxy</c> site — which is every
    /// other way to arrive here — carries <c>X-Forwarded-For</c> and <c>-Host</c>. So a request bearing any
    /// forwarding marker gets a bare 404: the same answer a nonexistent path gives, and identical for known
    /// and unknown domains, which is the property that keeps the route table from leaking. Spoofing a
    /// marker on the published port only makes the answer <em>less</em> informative, so the gate fails
    /// closed and there is no bypass in that direction.
    /// </para>
    /// <para>
    /// <c>X-Forwarded-Proto</c> is checked for completeness rather than as a load-bearing signal:
    /// <c>UseForwardedHeaders</c> runs first in the pipeline and consumes that one header (see Program.cs),
    /// so it is usually gone by the time this runs. <c>-For</c> and <c>-Host</c> are deliberately left
    /// untouched there and are what this actually keys on.
    /// </para>
    /// <para>
    /// The other half of the contract is integration reality that no unit test can pin: Caddy's ask call
    /// must keep arriving unmarked. If a future proxy configuration ever routed it through a site block,
    /// on-demand TLS would stop issuing certificates for custom domains — a loud failure rather than a
    /// silent one, which is the right direction, but this is where to look when it happens.
    /// </para>
    /// <para>
    /// Answered at all only while <c>Caddy</c> is the selected provider (ADR-0015, ADR-0022).
    /// It exists for one caller — Caddy's on-demand-TLS module — and the other two providers have no use
    /// for it: the in-process proxy reads its own route table straight out of memory, and Cloudflare's edge
    /// terminates TLS and never asks anyone whether a hostname is known. Under either of those the endpoint
    /// would be nothing but an oracle with no consumer, so it 404s like a path that was never mapped, and
    /// switching the provider at runtime moves it in and out of existence with no restart.
    /// </para>
    /// </remarks>
    private static void MapProxyAsk(WebApplication app) {
        app.MapGet("/api/proxy/ask", async (
            string? domain, HttpRequest request, WatchtowerDbContext db,
            IOptionsMonitor<WatchtowerOptions> options, CancellationToken ct) => {
            // Only Caddy's on-demand TLS has any use for this answer. Under the other providers the
            // endpoint would be a route-existence oracle that nothing asks — the in-process proxy holds the
            // route table in memory, and Cloudflare's edge terminates TLS — so it is simply not there.
            if (options.CurrentValue.Proxy.ResolveProvider() != ProxyProviderKind.Caddy)
                return Results.NotFound();
            if (ArrivedThroughTheProxy(request)) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(domain)) return Results.BadRequest();
            var known = await db.Routes.AsNoTracking()
                .AnyAsync(r => r.Domain == domain.Trim().ToLower(), ct);
            return known ? Results.Ok() : Results.StatusCode(StatusCodes.Status403Forbidden);
        });
    }

    /// <summary>
    /// Whether a Caddy <c>reverse_proxy</c> relayed this request rather than a component calling Watchtower
    /// directly, judged by the forwarding headers Caddy stamps on everything it proxies.
    /// </summary>
    private static bool ArrivedThroughTheProxy(HttpRequest request) =>
        request.Headers.ContainsKey("X-Forwarded-For") ||
        request.Headers.ContainsKey("X-Forwarded-Host") ||
        request.Headers.ContainsKey("X-Forwarded-Proto");

    /// <summary>
    /// Externally-facing deploy webhook. The stack must have <c>WebhookEnabled = true</c> (else 404, so
    /// the endpoint never reveals stack existence). When a token is set, the caller must supply
    /// <c>Authorization: Bearer {token}</c>.
    /// </summary>
    private static void MapWebhook(WebApplication app) {
        app.MapPost("/api/webhooks/stacks/{id:int}/deploy", async (
            int id, HttpRequest request, WatchtowerDbContext db, DeployQueueService deployQueue, CancellationToken ct) => {
            var stack = await db.Stacks.AsNoTracking()
                .Where(s => s.Id == id)
                .Select(s => new { s.WebhookEnabled, s.WebhookToken, s.DesiredState })
                .FirstOrDefaultAsync(ct);

            if (stack is null || !stack.WebhookEnabled)
                return Results.NotFound();

            if (!string.IsNullOrEmpty(stack.WebhookToken)) {
                var authHeader = request.Headers.Authorization.ToString();
                if (!string.Equals(authHeader, $"Bearer {stack.WebhookToken}", StringComparison.Ordinal))
                    return Results.Unauthorized();
            }

            // A stopped stack is deliberately disabled (ADR-0025); a CI push must not revive it.
            // After the token check, so only an authorized caller learns the state.
            if (stack.DesiredState == StackDesiredState.Stopped)
                return Results.Conflict("Stack is stopped — start it in Watchtower before deploying.");

            var result = deployQueue.Enqueue(id, "webhook");
            return Results.Accepted($"/api/stacks/{id}/events",
                new WebhookDeployResult(result.DeployEventId, result.Status));
        });
    }

    /// <summary>
    /// Streams deploy output lines for an event as Server-Sent Events. While running: replays buffered
    /// lines then streams live ones. After completion: replays the stored output from the database.
    /// </summary>
    private static RouteHandlerBuilder MapDeployOutputStream(WebApplication app) =>
        app.MapGet("/api/stacks/events/{eventId:int}/stream", async (
            int eventId, HttpResponse response, DeployOutputBroadcaster broadcaster,
            WatchtowerDbContext db, CancellationToken ct) => {
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            response.Headers.Append("X-Accel-Buffering", "no");

            var session = broadcaster.TryGet(eventId);

            if (session is null) {
                var stored = await db.DeployEvents.AsNoTracking()
                    .Where(e => e.Id == eventId).Select(e => e.Output).FirstOrDefaultAsync(ct);
                if (stored is not null)
                    foreach (var line in stored.Split('\n', StringSplitOptions.None))
                        await WriteSseLine(response, line.TrimEnd('\r'), ct);
            } else {
                var (history, live) = session.Subscribe();
                foreach (var line in history)
                    await WriteSseLine(response, line, ct);

                if (live is not null) {
                    try {
                        await foreach (var line in live.ReadAllAsync(ct))
                            await WriteSseLine(response, line, ct);
                    } catch (OperationCanceledException) {
                        return; // client disconnected or server shutting down
                    }
                }
            }

            await response.WriteAsync("event: done\ndata: \n\n", ct);
            await response.Body.FlushAsync(ct);
        });

    /// <summary>
    /// Streams a single named volume as a gzipped tar (entries rooted <c>backup/{volume}/…</c>),
    /// using the same never-started helper-container mechanism as stack backups (ADR-0016 §1) — so
    /// nothing is staged on disk and no code runs in the helper. A snapshot of a live volume is only
    /// crash-consistent; for consistent database archives use the backup flow, which can stop the
    /// stack's containers first.
    /// </summary>
    private static RouteHandlerBuilder MapVolumeDownload(WebApplication app) =>
        app.MapGet("/api/volumes/{name}/download", async (
            string name, HttpResponse response, DockerEngineClient docker,
            BackupArchiveService archiveService, IOptionsMonitor<WatchtowerOptions> options,
            CancellationToken ct) => {
            var volumes = await docker.ListVolumesAsync(ct);
            if (!volumes.Any(v => v.Name == name))
                return Results.NotFound();

            var fileName =
                $"{BackupNaming.Sanitize(name)}_{DateTimeOffset.UtcNow.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.tar.gz";
            response.ContentType = "application/gzip";
            response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
            // Fastest, not Optimal: this is an interactive download, and volume bytes are typically
            // already-compressed application data; latency beats ratio here.
            await using (var gzip = new GZipStream(response.Body, CompressionLevel.Fastest, leaveOpen: true))
                await archiveService.WriteArchiveAsync(
                    [name], manifestJson: null, gzip, options.CurrentValue.Backup.HelperImage, ct);
            return Results.Empty;
        });

    /// <summary>Streams container logs as Server-Sent Events. Query: tail (default 100), follow (default true).</summary>
    private static RouteHandlerBuilder MapContainerLogStream(WebApplication app) =>
        app.MapGet("/api/containers/{id}/logs", async (
            string id, int? tail, bool? follow, HttpResponse response, DockerEngineClient docker, CancellationToken ct) => {
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-cache";
            response.Headers.Append("X-Accel-Buffering", "no");

            await foreach (var line in docker.StreamLogsAsync(id, tail ?? 100, follow ?? true, ct)) {
                var escaped = line.Replace("\r", "").Replace("\n", "\\n");
                await response.WriteAsync($"data: {escaped}\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
        });

    /// <summary>Writes one SSE data line; embedded newlines are escaped to preserve the frame boundary.</summary>
    private static async Task WriteSseLine(HttpResponse response, string line, CancellationToken ct) {
        var escaped = line.Replace("\r", "").Replace("\n", "\\n");
        await response.WriteAsync($"data: {escaped}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
