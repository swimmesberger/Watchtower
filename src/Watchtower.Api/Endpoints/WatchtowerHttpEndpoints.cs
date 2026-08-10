using Microsoft.EntityFrameworkCore;
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
    /// Maps the non-RPC surfaces: the deploy webhook, the two SSE streams, the proxy TLS gate, the public
    /// App API, and <c>/health</c>. With <paramref name="authEnabled"/> the two SSE streams — which carry
    /// deploy output and container logs, i.e. exactly the data the JSON-RPC handlers now protect — require a
    /// login session too; <c>EventSource</c> sends cookies on same-origin requests, so the UI is unaffected.
    /// </summary>
    /// <remarks>
    /// Endpoints that stay open by design (design.md §11): <c>/health</c> is a liveness probe with no data,
    /// the deploy webhook authenticates callers with its own per-stack bearer token (a CI runner has no
    /// browser session), <c>/api/proxy/ask</c> is Caddy's on-demand-TLS gate reachable only from the internal
    /// control network, and the App API and management API carry their own per-stack token auth (see
    /// AppApiEndpoints and MgmtApiEndpoints).
    /// </remarks>
    public static WebApplication MapWatchtowerHttpEndpoints(this WebApplication app, bool authEnabled) {
        MapWebhook(app);
        Protect(MapDeployOutputStream(app), authEnabled);
        Protect(MapContainerLogStream(app), authEnabled);
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

    /// <summary>Requires a valid login session on an endpoint, but only when authentication is configured.</summary>
    private static void Protect(RouteHandlerBuilder route, bool authEnabled) {
        if (authEnabled) route.RequireAuthorization();
    }

    /// <summary>
    /// On-demand TLS gate for Caddy (custom domains). Caddy calls this before issuing a certificate for
    /// a requested host; we return 200 only for domains that exist in the route table, so a stray domain
    /// pointed at this host can never trigger unbounded certificate issuance. Reachable only on the
    /// internal control network.
    /// </summary>
    private static void MapProxyAsk(WebApplication app) {
        app.MapGet("/api/proxy/ask", async (string? domain, WatchtowerDbContext db, CancellationToken ct) => {
            if (string.IsNullOrWhiteSpace(domain)) return Results.BadRequest();
            var known = await db.Routes.AsNoTracking()
                .AnyAsync(r => r.Domain == domain.Trim().ToLower(), ct);
            return known ? Results.Ok() : Results.StatusCode(StatusCodes.Status403Forbidden);
        });
    }

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
                .Select(s => new { s.WebhookEnabled, s.WebhookToken })
                .FirstOrDefaultAsync(ct);

            if (stack is null || !stack.WebhookEnabled)
                return Results.NotFound();

            if (!string.IsNullOrEmpty(stack.WebhookToken)) {
                var authHeader = request.Headers.Authorization.ToString();
                if (!string.Equals(authHeader, $"Bearer {stack.WebhookToken}", StringComparison.Ordinal))
                    return Results.Unauthorized();
            }

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
