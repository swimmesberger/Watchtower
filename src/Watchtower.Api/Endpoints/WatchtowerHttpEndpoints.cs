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

    public static WebApplication MapWatchtowerHttpEndpoints(this WebApplication app) {
        MapWebhook(app);
        MapDeployOutputStream(app);
        MapContainerLogStream(app);
        app.MapGet("/health", () => Results.Ok("healthy"));
        return app;
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
    private static void MapDeployOutputStream(WebApplication app) {
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
    }

    /// <summary>Streams container logs as Server-Sent Events. Query: tail (default 100), follow (default true).</summary>
    private static void MapContainerLogStream(WebApplication app) {
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
    }

    /// <summary>Writes one SSE data line; embedded newlines are escaped to preserve the frame boundary.</summary>
    private static async Task WriteSseLine(HttpResponse response, string line, CancellationToken ct) {
        var escaped = line.Replace("\r", "").Replace("\n", "\\n");
        await response.WriteAsync($"data: {escaped}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
