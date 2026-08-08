using System.Text.Json.Serialization;
using System.Threading.Channels;
using Watchtower.Application.Services;

namespace Watchtower.Api.Endpoints;

/// <summary>
/// The public **App API** (<c>/api/app/*</c>): a token-authenticated REST surface that applications
/// deployed by Watchtower call to query <em>their own</em> deployment status, deployed version and
/// logs.
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint authenticates with <c>Authorization: Bearer &lt;token&gt;</c>, where the token is the
/// one Watchtower injected into the stack's environment as <c>WATCHTOWER_APP_TOKEN</c> at deploy time.
/// A missing, malformed or unknown token yields 401; a token belonging to a stack whose App API has
/// been switched off yields 403.
/// </para>
/// <para>
/// Container ids are never accepted from the caller — every Docker lookup is resolved server-side
/// from the authenticated stack's compose project label, so a stack can only ever observe itself.
/// Responses deliberately exclude deploy output, environment variable values and credentials.
/// </para>
/// <para>
/// Per ADR-0003 these are plain minimal-API routes rather than JSON-RPC handlers: they are externally
/// facing with their own auth semantics, and one of them is a stream.
/// </para>
/// </remarks>
public static class AppApiEndpoints {
    /// <summary>Maps every <c>/api/app/*</c> route onto the application.</summary>
    /// <param name="app">The web application to map onto.</param>
    /// <returns>The same application, for chaining.</returns>
    public static WebApplication MapAppApiEndpoints(this WebApplication app) {
        MapSelf(app);
        MapStatus(app);
        MapDeployments(app);
        MapVersion(app);
        MapLogs(app);
        return app;
    }

    /// <summary>Identity of the calling stack.</summary>
    private static void MapSelf(WebApplication app) =>
        app.MapGet("/api/app/self", async (HttpRequest request, AppApiService api, CancellationToken ct) => {
            var (failure, caller) = await AuthenticateAsync(request, api, ct);
            return failure ?? Results.Json(AppApiService.GetSelf(caller!));
        });

    /// <summary>Last-deploy metadata, the in-flight deploy if any, and live per-service container state.</summary>
    private static void MapStatus(WebApplication app) =>
        app.MapGet("/api/app/status", async (HttpRequest request, AppApiService api, CancellationToken ct) => {
            var (failure, caller) = await AuthenticateAsync(request, api, ct);
            if (failure is not null) return failure;
            try {
                return Results.Json(await api.GetStatusAsync(caller!, ct));
            } catch (HttpRequestException) {
                return DockerUnavailable();
            }
        });

    /// <summary>
    /// Recent deploy events, newest first. <c>limit</c> defaults to 20 and is capped at 100. The
    /// captured command output is never included — it is produced with credentials in scope.
    /// </summary>
    private static void MapDeployments(WebApplication app) =>
        app.MapGet("/api/app/deployments", async (
            int? limit, HttpRequest request, AppApiService api, CancellationToken ct) => {
            var (failure, caller) = await AuthenticateAsync(request, api, ct);
            return failure ?? Results.Json(await api.GetDeploymentsAsync(caller!, limit, ct));
        });

    /// <summary>Deployed commit plus the live image identity of every service container.</summary>
    private static void MapVersion(WebApplication app) =>
        app.MapGet("/api/app/version", async (HttpRequest request, AppApiService api, CancellationToken ct) => {
            var (failure, caller) = await AuthenticateAsync(request, api, ct);
            if (failure is not null) return failure;
            try {
                return Results.Json(await api.GetVersionAsync(caller!, ct));
            } catch (HttpRequestException) {
                return DockerUnavailable();
            }
        });

    /// <summary>
    /// Streams the calling stack's container logs as Server-Sent Events, using the same SSE mechanics
    /// as the operator-facing <c>/api/containers/{id}/logs</c> stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The target containers are resolved from the authenticated stack's compose project label.
    /// Ambiguity is judged by distinct <em>service</em> names, not container count: when the stack
    /// exposes more than one service and no <c>service</c> was given the request is rejected with 400
    /// and a body listing the available names, rather than guessing.
    /// </para>
    /// <para>
    /// A service scaled to several replicas is not ambiguous — all of its containers are merged into
    /// the one stream, and every line is prefixed with the emitting container's 12-character short id
    /// (<c>abc123def456 | …</c>) so the replicas stay distinguishable. A single matching container
    /// streams unprefixed.
    /// </para>
    /// <para>
    /// <c>tail</c> defaults to 100 and is clamped to 5000. <c>follow</c> defaults to <c>false</c> so a
    /// plain call returns a bounded response and terminates with an <c>event: done</c> frame; pass
    /// <c>follow=true</c> to keep the stream open.
    /// </para>
    /// </remarks>
    private static void MapLogs(WebApplication app) =>
        app.MapGet("/api/app/logs", async (
            string? service, int? tail, bool? follow,
            HttpRequest request, HttpResponse response,
            AppApiService api, DockerEngineClient docker, CancellationToken ct) => {
            var (failure, caller) = await AuthenticateAsync(request, api, ct);
            if (failure is not null) return failure;

            IReadOnlyList<DockerContainerInfo> containers;
            try {
                containers = await api.ListMemberContainersAsync(caller!, service, ct);
            } catch (HttpRequestException) {
                // Still before any body was written, so a normal status response is possible.
                return DockerUnavailable();
            }

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
        });

    /// <summary>
    /// Streams one or more containers' logs into a single SSE response.
    /// </summary>
    /// <remarks>
    /// With several containers each is pumped by its own task into a bounded channel, so a slow
    /// consumer applies backpressure instead of buffering without limit, and lines are prefixed with
    /// the emitting container's short id. Whatever ends the response — completion, client
    /// disconnect, or a Docker failure — every inner stream is cancelled and awaited before
    /// returning, so no enumeration outlives the request.
    /// </remarks>
    /// <param name="response">Response to write SSE frames to; headers are already sent.</param>
    /// <param name="docker">Docker Engine API client.</param>
    /// <param name="containers">Containers to stream, already scoped to the caller's own stack.</param>
    /// <param name="tail">Number of historical lines to replay per container.</param>
    /// <param name="follow">Whether to keep following new output.</param>
    /// <param name="ct">Request cancellation token.</param>
    private static async Task StreamContainerLogsAsync(
        HttpResponse response, DockerEngineClient docker,
        IReadOnlyList<DockerContainerInfo> containers, int tail, bool follow, CancellationToken ct) {
        var prefixWithId = containers.Count > 1;
        var channel = Channel.CreateBounded<string>(new BoundedChannelOptions(512) {
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
            await foreach (var line in channel.Reader.ReadAllAsync(ct)) {
                await response.WriteAsync($"data: {line}\n\n", ct);
                await response.Body.FlushAsync(ct);
            }
            await WriteSseEventAsync(response, "done", string.Empty, ct);
        } catch (OperationCanceledException) {
            // Client disconnected or the server is shutting down — nothing left to write.
        } catch (HttpRequestException) {
            // Headers are already out, so the failure is reported in-band rather than as a status.
            await WriteSseEventAsync(response, "error", "The Docker daemon became unreachable.", CancellationToken.None);
        } finally {
            await producerCts.CancelAsync();
            try { await pumping; } catch { /* surfaced through the channel already */ }
        }
    }

    /// <summary>Pumps one container's log lines into the shared channel, escaping SSE frame breaks.</summary>
    private static async Task PumpContainerLogsAsync(
        DockerEngineClient docker, DockerContainerInfo container, bool prefixWithId,
        int tail, bool follow, ChannelWriter<string> writer, CancellationToken ct) {
        var shortId = container.Id.Length >= 12 ? container.Id[..12] : container.Id;
        await foreach (var line in docker.StreamLogsAsync(container.Id, tail, follow, ct)) {
            var escaped = line.Replace("\r", "").Replace("\n", "\\n");
            await writer.WriteAsync(prefixWithId ? $"{shortId} | {escaped}" : escaped, ct);
        }
    }

    /// <summary>
    /// Completes the channel once every producer has finished, propagating the first failure to the
    /// reader so the endpoint can report it. Cancellation is a normal end, not a failure.
    /// </summary>
    private static async Task CompleteChannelWhenDoneAsync(List<Task> producers, ChannelWriter<string> writer) {
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

    /// <summary>503 response used when the Docker daemon cannot be reached.</summary>
    private static IResult DockerUnavailable() =>
        Results.Json(new AppApiErrorDto("The Docker daemon is currently unreachable."),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// Authenticates the request, translating a rejection into the response to return.
    /// </summary>
    /// <param name="request">Incoming request; only its <c>Authorization</c> header is read.</param>
    /// <param name="api">The App API service performing the lookup.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Either a non-null failure result (401/403) to return verbatim, or a non-null caller to serve.
    /// </returns>
    private static async Task<(IResult? Failure, AppApiCaller? Caller)> AuthenticateAsync(
        HttpRequest request, AppApiService api, CancellationToken ct) {
        var result = await api.AuthenticateAsync(request.Headers.Authorization.ToString(), ct);
        return result.Status switch {
            AppApiAuthStatus.Ok => (null, result.Caller),
            AppApiAuthStatus.Forbidden => (
                Results.Json(new AppApiErrorDto("The App API is disabled for this stack."),
                    statusCode: StatusCodes.Status403Forbidden), null),
            _ => (
                Results.Json(new AppApiErrorDto("Missing or invalid App API token."),
                    statusCode: StatusCodes.Status401Unauthorized), null),
        };
    }
}

/// <summary>
/// Source-generated JSON metadata for the host's plain-HTTP response bodies (the App API DTOs and the
/// webhook result). Registered into the minimal-API serializer chain in <c>Program.cs</c> so these
/// responses never depend on reflection-based serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSelfDto))]
[JsonSerializable(typeof(AppStatusDto))]
[JsonSerializable(typeof(AppDeploymentsDto))]
[JsonSerializable(typeof(AppVersionDto))]
[JsonSerializable(typeof(AppApiErrorDto))]
[JsonSerializable(typeof(WatchtowerHttpEndpoints.WebhookDeployResult))]
public sealed partial class WatchtowerHttpJsonContext : JsonSerializerContext;
