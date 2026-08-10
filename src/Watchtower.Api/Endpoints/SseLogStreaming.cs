using System.Threading.Channels;
using Watchtower.Application.Services;

namespace Watchtower.Api.Endpoints;

/// <summary>
/// The container-log Server-Sent-Event contract shared by the public App API
/// (<c>GET /api/app/logs</c>) and the public management API
/// (<c>GET /api/mgmt/templates/{id}/tenants/{slug}/logs</c>).
/// </summary>
/// <remarks>
/// <para>
/// Both surfaces stream the logs of a stack the caller is entitled to, and both must behave identically
/// down to the frame level — ambiguity handling, replica prefixes, per-source error frames and the
/// terminal <c>done</c>. Keeping that in one place is the point: a second copy would drift, and the two
/// copies would drift in exactly the details a client cannot discover for itself.
/// </para>
/// <para>
/// What stays with the caller is <em>which</em> containers to stream. This helper never resolves a
/// container from anything in the request — it is handed an already-scoped list (from the authenticated
/// stack's compose project label, or from the resolved tenant's) and streams precisely that.
/// </para>
/// </remarks>
internal static class SseLogStreaming {
    /// <summary>
    /// Serves an already-scoped container set as an SSE log stream, applying the shared rejection rules
    /// first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ambiguity is judged by distinct <em>service</em> names, not container count: when the stack
    /// exposes more than one service and no <paramref name="service"/> was given the request is rejected
    /// with 400 and a body listing the available names, rather than guessing.
    /// </para>
    /// <para>
    /// A service scaled to several replicas is not ambiguous — all of its containers are merged into the
    /// one stream, and every line is prefixed with the emitting container's 12-character short id
    /// (<c>abc123def456 | …</c>) so the replicas stay distinguishable. A single matching container
    /// streams unprefixed.
    /// </para>
    /// <para>
    /// <paramref name="tail"/> defaults to 100 and is clamped to 5000. <paramref name="follow"/> defaults
    /// to <c>false</c> so a plain call returns a bounded response and terminates with an
    /// <c>event: done</c> frame; pass <c>follow=true</c> to keep the stream open.
    /// </para>
    /// </remarks>
    /// <param name="response">Response to serve on; headers have not been sent yet.</param>
    /// <param name="docker">Docker Engine API client.</param>
    /// <param name="containers">Containers to stream, already scoped to what the caller may observe.</param>
    /// <param name="service">The requested compose service, echoed into the 404 message; null when unfiltered.</param>
    /// <param name="tail">Requested history per container; null uses the default.</param>
    /// <param name="follow">Whether to keep following new output; null means false.</param>
    /// <param name="ct">Request cancellation token.</param>
    /// <returns>
    /// A 400/404 result when the request is rejected before anything is written, or
    /// <see cref="Results.Empty"/> once the stream has been served in full.
    /// </returns>
    public static async Task<IResult> ServeLogsAsync(
        HttpResponse response, DockerEngineClient docker,
        IReadOnlyList<DockerContainerInfo> containers,
        string? service, int? tail, bool? follow, CancellationToken ct) {
        if (containers.Count == 0)
            return Results.Json(new AppApiErrorDto(string.IsNullOrEmpty(service)
                    ? "This stack has no containers."
                    : $"No container found for service '{service}'."),
                statusCode: StatusCodes.Status404NotFound);

        var serviceNames = AppApiService.ServiceNames(containers);
        if (serviceNames.Count > 1 && string.IsNullOrEmpty(service))
            return Results.Json(new AppApiErrorDto(
                    "This stack has multiple services; specify ?service=<name>.",
                    serviceNames),
                statusCode: StatusCodes.Status400BadRequest);

        var tailLines = Math.Clamp(tail ?? AppApiService.DefaultLogTail, 1, AppApiService.MaxLogTail);

        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Append("X-Accel-Buffering", "no");

        await StreamContainerLogsAsync(response, docker, containers, tailLines, follow ?? false, ct);
        return Results.Empty;
    }

    /// <summary>
    /// Streams one or more containers' logs into a single SSE response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// With several containers each is pumped by its own task into a bounded channel, so a slow
    /// consumer applies backpressure instead of buffering without limit, and lines are prefixed with
    /// the emitting container's short id.
    /// </para>
    /// <para>
    /// One replica failing does not end the response: that producer emits an <c>event: error</c> frame
    /// naming its container and stops, while the surviving replicas keep streaming. The stream ends
    /// only when every producer has finished (then <c>event: done</c>) or the client disconnects.
    /// </para>
    /// <para>
    /// Whatever ends the response — completion, client disconnect, or failure — every inner stream is
    /// cancelled and awaited before returning, so no enumeration outlives the request.
    /// </para>
    /// </remarks>
    /// <param name="response">Response to write SSE frames to; headers are already sent.</param>
    /// <param name="docker">Docker Engine API client.</param>
    /// <param name="containers">Containers to stream, already scoped to what the caller may observe.</param>
    /// <param name="tail">Number of historical lines replayed per container (each replica replays this many).</param>
    /// <param name="follow">Whether to keep following new output.</param>
    /// <param name="ct">Request cancellation token.</param>
    private static async Task StreamContainerLogsAsync(
        HttpResponse response, DockerEngineClient docker,
        IReadOnlyList<DockerContainerInfo> containers, int tail, bool follow, CancellationToken ct) {
        var prefixWithId = containers.Count > 1;
        var channel = Channel.CreateBounded<LogFrame>(new BoundedChannelOptions(512) {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = containers.Count == 1,
        });

        using var producerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producers = containers
            .Select(c => PumpContainerLogsAsync(docker, c, prefixWithId, tail, follow, channel.Writer, producerCts.Token))
            .ToList();
        var pumping = CompleteChannelWhenDoneAsync(producers, channel.Writer);

        try {
            await foreach (var frame in channel.Reader.ReadAllAsync(ct)) {
                if (frame.IsError)
                    await WriteSseEventAsync(response, "error", frame.Data, ct);
                else
                    await response.WriteAsync($"data: {frame.Data}\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
            await WriteSseEventAsync(response, "done", string.Empty, ct);
        } catch (OperationCanceledException) {
            // Client disconnected or the server is shutting down — nothing left to write.
        } catch (Exception) {
            // Headers are already out, so a failure is reported in-band rather than as a status.
            await WriteSseEventAsync(response, "error", "The log stream failed.", CancellationToken.None);
        } finally {
            await producerCts.CancelAsync();
            try { await pumping; } catch { /* already reported in band */ }
        }
    }

    /// <summary>One SSE frame queued by a producer: either a log line or an in-band error notice.</summary>
    /// <param name="Data">Frame payload, already escaped so it cannot break the SSE framing.</param>
    /// <param name="IsError">True when the frame should be emitted as <c>event: error</c>.</param>
    private readonly record struct LogFrame(string Data, bool IsError);

    /// <summary>
    /// Pumps one container's log lines into the shared channel, escaping SSE frame breaks.
    /// </summary>
    /// <remarks>
    /// A failure of <em>this</em> container is reported as an in-band error frame and ends only this
    /// producer — the other replicas keep streaming. The exception is deliberately not rethrown, so
    /// one dead replica cannot tear down a healthy multi-replica stream, and the message is a fixed
    /// string rather than the exception text so nothing internal leaks to the caller.
    /// </remarks>
    private static async Task PumpContainerLogsAsync(
        DockerEngineClient docker, DockerContainerInfo container, bool prefixWithId,
        int tail, bool follow, ChannelWriter<LogFrame> writer, CancellationToken ct) {
        var shortId = container.Id.Length >= 12 ? container.Id[..12] : container.Id;
        try {
            await foreach (var line in docker.StreamLogsAsync(container.Id, tail, follow, ct)) {
                var escaped = line.Replace("\r", "").Replace("\n", "\\n");
                await writer.WriteAsync(new LogFrame(prefixWithId ? $"{shortId} | {escaped}" : escaped, false), ct);
            }
        } catch (OperationCanceledException) {
            // Normal end: the client disconnected or the response finished.
        } catch (Exception ex) {
            // Error frames always name the container, even single-container streams, because the
            // whole point of the frame is to say which log source stopped.
            var reason = ex is HttpRequestException
                ? "log stream failed (the Docker daemon is unreachable)"
                : "log stream failed";
            try {
                await writer.WriteAsync(new LogFrame($"{shortId} | {reason}", true), ct);
            } catch (Exception) {
                // The consumer is already gone; there is nowhere to report this.
            }
        }
    }

    /// <summary>
    /// Completes the channel once every producer has finished.
    /// </summary>
    /// <remarks>
    /// Producers report their own failures in band, so this normally completes cleanly even when some
    /// replicas died — the stream ends when <em>all</em> producers are done or the client disconnects,
    /// never on the first failure. The error path remains for a fault outside a producer's own
    /// try/catch, which the reader then surfaces as a final error frame.
    /// </remarks>
    private static async Task CompleteChannelWhenDoneAsync(List<Task> producers, ChannelWriter<LogFrame> writer) {
        try {
            await Task.WhenAll(producers);
            writer.TryComplete();
        } catch (OperationCanceledException) {
            writer.TryComplete();
        } catch (Exception ex) {
            writer.TryComplete(ex);
        }
    }

    /// <summary>Writes a named SSE event, swallowing failures on an already-broken connection.</summary>
    private static async Task WriteSseEventAsync(
        HttpResponse response, string eventName, string data, CancellationToken ct) {
        try {
            await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct);
            await response.Body.FlushAsync(ct);
        } catch (Exception) {
            // The client is gone; there is nowhere left to report this.
        }
    }
}
