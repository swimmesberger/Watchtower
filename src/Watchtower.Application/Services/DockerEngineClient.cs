using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// Communicates with the Docker Engine API over the Unix domain socket at /var/run/docker.sock.
/// Uses persistent HttpClients configured with a custom SocketsHttpHandler so no
/// real TCP connection is made — the socket path is passed as the "host" in requests.
/// </summary>
/// <remarks>
/// Two clients share the one handler (and therefore one connection pool). Almost every call is
/// UI-facing and keeps HttpClient's 100-second default timeout, so a wedged daemon socket fails
/// fast instead of hanging a page. The few calls whose duration is a property of the host rather
/// than of Watchtower — the container wait, the image prune, the container-archive read and the
/// exec start — go through a second, untimed client: the 100-second ceiling would abandon them
/// mid-flight (a self-update watch, a prune of months of accumulated layers, a volume archive, or
/// a database dump) even though nothing is wrong. Streamed calls
/// (<see cref="HttpCompletionOption.ResponseHeadersRead"/>) get their headers at once, but the
/// ceiling keeps running while the body is read, so a body that is both long-lived and silently
/// truncatable — an exec's output, where a cut-off dump looks exactly like a complete one — needs
/// the untimed client just as much. The log stream does not: it is a page's worth of text, and a
/// wedged daemon should fail it fast.
/// </remarks>
public sealed class DockerEngineClient : IDisposable {
    /// <summary>
    /// Client-side ceiling for <see cref="PruneImagesAsync"/>. The untimed client removes HttpClient's
    /// bound, and the prune runs from a background loop that has nothing but this to stop it parking
    /// forever on a daemon that never answers. Generous on purpose: a first prune on a host with a long
    /// backlog of layers is legitimately slow, and exceeding this is reported as a failure.
    /// </summary>
    internal static readonly TimeSpan PruneTimeout = TimeSpan.FromMinutes(30);

    private readonly HttpClient _client;
    private readonly HttpClient _longRunningClient;
    /// <summary>Non-null only when this instance built the handler and therefore has to dispose it.</summary>
    private readonly HttpMessageHandler? _ownedHandler;
    private readonly TimeSpan _pruneTimeout;
    private readonly string _apiBase;

    /// <param name="options">
    /// Watchtower options — reads <c>DockerApiVersion</c> to build the API base path
    /// (e.g. <c>/v1.43</c>). This is the same version used by <see cref="ComposeCliService"/>
    /// via <c>DOCKER_API_VERSION</c>, ensuring both communicate with the daemon at the same level.
    /// </param>
    public DockerEngineClient(IOptions<WatchtowerOptions> options)
        : this(options.Value.DockerApiVersion, CreateSocketHandler(), PruneTimeout) { }

    /// <summary>
    /// Builds both clients over <paramref name="ownedHandler"/> and takes responsibility for
    /// disposing it. This is the shape the production constructor uses; a test reaches it directly
    /// to cover the disposal path without needing a Docker socket.
    /// </summary>
    internal DockerEngineClient(string apiVersion, HttpMessageHandler ownedHandler, TimeSpan pruneTimeout) {
        _apiBase = $"/v{apiVersion}";
        _pruneTimeout = pruneTimeout;
        _ownedHandler = ownedHandler;
        (_client, _longRunningClient) = CreateClients(ownedHandler);
    }

    /// <summary>The Unix-socket handler: every request is routed to /var/run/docker.sock.</summary>
    private static SocketsHttpHandler CreateSocketHandler() => new() {
        ConnectCallback = async (ctx, ct) => {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
    };

    /// <summary>
    /// Test seam: takes the two clients ready-made so a test can tell apart which one a call was
    /// routed through. The handler is the caller's to dispose in this shape.
    /// </summary>
    internal DockerEngineClient(
        string apiVersion, HttpClient client, HttpClient longRunningClient, TimeSpan pruneTimeout) {
        _apiBase = $"/v{apiVersion}";
        _client = client;
        _longRunningClient = longRunningClient;
        _pruneTimeout = pruneTimeout;
        _ownedHandler = null;
    }

    /// <summary>
    /// Builds the default and long-running clients over a single shared <paramref name="handler"/>:
    /// one connection pool, two timeout policies. Neither client is given ownership of the handler
    /// (<c>disposeHandler: false</c>) — <see cref="Dispose"/> disposes it exactly once instead, which
    /// is also what keeps the second client from tearing the pool out from under the first.
    /// </summary>
    internal static (HttpClient Default, HttpClient LongRunning) CreateClients(HttpMessageHandler handler) {
        // The hostname is ignored when using a Unix socket; "docker" is used for clarity in logs.
        var baseAddress = new Uri("http://docker");
        // Deliberately left at HttpClient's 100-second default — see the class remarks.
        var client = new HttpClient(handler, disposeHandler: false) { BaseAddress = baseAddress };
        var longRunning = new HttpClient(handler, disposeHandler: false) {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        return (client, longRunning);
    }

    /// <summary>
    /// Returns all running containers from the Docker Engine API,
    /// enriched with compose project label metadata.
    /// </summary>
    public async Task<IReadOnlyList<DockerContainerInfo>> ListContainersAsync(CancellationToken ct = default) {
        // Default (omit all or all=0) returns only running containers.
        var response = await _client.GetAsync($"{_apiBase}/containers/json", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(json, DockerJsonContext.Default.ListDockerContainerInfo, ct)
            ?? [];
    }

    /// <summary>Sends a restart signal to the specified container.</summary>
    public async Task RestartContainerAsync(string containerId, CancellationToken ct = default) {
        var response = await _client.PostAsync($"{_apiBase}/containers/{containerId}/restart", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Sends a stop signal to the specified container (SIGTERM → SIGKILL after the daemon's default 10 s).</summary>
    public Task StopContainerAsync(string containerId, CancellationToken ct = default) =>
        StopContainerAsync(containerId, timeoutSeconds: null, ct);

    /// <summary>
    /// Sends a stop signal to the specified container, giving it <paramref name="timeoutSeconds"/> to
    /// exit on SIGTERM before the daemon sends SIGKILL (<c>?t=N</c>). Null keeps the daemon's default
    /// (10 s, or the container's own <c>--stop-timeout</c>).
    /// </summary>
    public async Task StopContainerAsync(string containerId, int? timeoutSeconds, CancellationToken ct = default) {
        var url = $"{_apiBase}/containers/{containerId}/stop";
        if (timeoutSeconds is { } t) url += $"?t={t}";
        var response = await _client.PostAsync(url, content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Freezes the container's processes with the cgroup freezer (<c>docker pause</c>): no signal is
    /// delivered, nothing exits, TCP connections stay open — the processes simply make no progress until
    /// <see cref="UnpauseContainerAsync"/>. Returns in milliseconds.
    /// </summary>
    public async Task PauseContainerAsync(string containerId, CancellationToken ct = default) {
        var response = await _client.PostAsync($"{_apiBase}/containers/{containerId}/pause", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Thaws a container frozen by <see cref="PauseContainerAsync"/> (<c>docker unpause</c>).</summary>
    public async Task UnpauseContainerAsync(string containerId, CancellationToken ct = default) {
        var response = await _client.PostAsync($"{_apiBase}/containers/{containerId}/unpause", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Removes a stopped container. The container must be stopped first.</summary>
    public async Task RemoveContainerAsync(string containerId, CancellationToken ct = default) {
        var response = await _client.DeleteAsync($"{_apiBase}/containers/{containerId}", ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Streams log lines from a container using the Docker Engine API.
    /// Demultiplexes Docker's binary frame format (8-byte header per frame).
    /// Each yielded string is one log line (newline stripped).
    ///
    /// Note: containers started with TTY=true do not use the multiplexed format —
    /// the stream is treated as raw text in that case.
    /// </summary>
    public async IAsyncEnumerable<string> StreamLogsAsync(
        string containerId,
        int tail = 100,
        bool follow = false,
        [EnumeratorCancellation] CancellationToken ct = default) {
        var url = $"{_apiBase}/containers/{containerId}/logs?stdout=1&stderr=1&tail={tail}&follow={(follow ? 1 : 0)}";

        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        // Logs mix both streams into one view on purpose: the frame's stream type is what tells
        // stdout from stderr, and the log viewer shows them interleaved as the container wrote them.
        await foreach (var frame in DockerStreamFrames.ReadAsync(stream, ct)) {
            var text = Encoding.UTF8.GetString(frame.Payload).TrimEnd('\n', '\r');
            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }

    /// <summary>
    /// Creates a new container from <paramref name="body"/> and returns its ID.
    /// The container is not started — call <see cref="StartContainerAsync"/> afterwards.
    /// </summary>
    public async Task<string> CreateContainerAsync(
        DockerCreateContainerBody body, string? name = null, CancellationToken ct = default) {
        var url = name is not null
            ? $"{_apiBase}/containers/create?name={Uri.EscapeDataString(name)}"
            : $"{_apiBase}/containers/create";
        var json = JsonSerializer.Serialize(body, DockerJsonContext.Default.DockerCreateContainerBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, DockerJsonContext.Default.DockerCreateContainerResponse, ct)
            ?? throw new InvalidOperationException("Null response creating container");
        return result.Id;
    }

    /// <summary>Starts a previously created container by ID.</summary>
    public async Task StartContainerAsync(string containerId, CancellationToken ct = default) {
        var response = await _client.PostAsync($"{_apiBase}/containers/{containerId}/start", content: null, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Blocks until the container is no longer running and returns its exit code.
    /// Uses Docker's <c>POST /containers/{id}/wait</c> endpoint, which is more
    /// efficient than polling <c>InspectContainerAsync</c>.
    /// </summary>
    /// <remarks>
    /// Goes through the untimed client: the daemon holds the response open for as long as the
    /// container keeps running, which the 100-second default would cut short — a self-update watch
    /// on a container that takes longer than that would fail for no reason other than the clock.
    /// How long the wait may last is the caller's business, expressed through <paramref name="ct"/>,
    /// which is the only bound on this call.
    /// </remarks>
    public async Task<int> WaitContainerAsync(string containerId, CancellationToken ct = default) {
        var response = await _longRunningClient.PostAsync($"{_apiBase}/containers/{containerId}/wait?condition=not-running", content: null, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, DockerJsonContext.Default.DockerWaitContainerResponse, ct)
            ?? throw new InvalidOperationException("Null response waiting for container");
        return result.StatusCode;
    }

    // ── Container filesystem archives ────────────────────────────────────────

    /// <summary>
    /// Streams a tar archive of <paramref name="path"/> inside the container via
    /// <c>GET /containers/{id}/archive</c>. Works on created (never started) containers, which is
    /// what the backup helper containers rely on (ADR-0016 §1): the daemon reads the files itself,
    /// nothing executes in the container. The returned stream is the live response body — dispose it
    /// to release the connection.
    /// </summary>
    /// <remarks>
    /// Long-running client: how long the archive takes is a property of the volume size, not of
    /// Watchtower, so the caller bounds it through <paramref name="ct"/> alone. The requested
    /// directory arrives as the top-level entry of the tar (e.g. <c>backup/…</c>).
    /// </remarks>
    public async Task<Stream> GetContainerArchiveAsync(
        string containerId, string path, CancellationToken ct = default) {
        var url = $"{_apiBase}/containers/{containerId}/archive?path={Uri.EscapeDataString(path)}";
        var response = await _longRunningClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        try {
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(ct);
        } catch {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Extracts a tar archive into <paramref name="path"/> inside the container via
    /// <c>PUT /containers/{id}/archive</c>. Also works on created containers — used to inject the
    /// backup manifest next to the mounted volumes before the archive is read back.
    /// </summary>
    public Task PutContainerArchiveAsync(
        string containerId, string path, Stream tarStream, CancellationToken ct = default) =>
        PutContainerArchiveAsync(
            containerId, path, (destination, token) => tarStream.CopyToAsync(destination, token), ct);

    /// <summary>
    /// Same as the stream overload, but hands <paramref name="writeTar"/> the request body to write
    /// the tar into directly — the shape <see cref="IBackupStorage.UploadAsync"/> uses. Nothing is
    /// staged in memory or on disk in between, which is what lets a dump of arbitrary size be
    /// injected next to the mounted volumes.
    /// </summary>
    public async Task PutContainerArchiveAsync(
        string containerId, string path, Func<Stream, CancellationToken, Task> writeTar,
        CancellationToken ct = default) {
        var url = $"{_apiBase}/containers/{containerId}/archive?path={Uri.EscapeDataString(path)}";
        using var content = new PushStreamContent(writeTar, ct);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-tar");
        var response = await _client.PutAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// An <see cref="HttpContent"/> whose body is produced by a callback writing into the request
    /// stream. <see cref="TryComputeLength"/> answers false because the length genuinely is not
    /// known up front, which sends the body chunked — the daemon accepts that, and the alternative
    /// is buffering the whole tar to measure it.
    /// </summary>
    private sealed class PushStreamContent(
        Func<Stream, CancellationToken, Task> writer, CancellationToken ct) : HttpContent {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            writer(stream, ct);

        protected override Task SerializeToStreamAsync(
            Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken) =>
            writer(stream, cancellationToken);

        protected override bool TryComputeLength(out long length) {
            length = 0;
            return false;
        }
    }

    // ── Exec ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Content type Docker answers an exec start with when the output carries no framing — a TTY
    /// exec, or a daemon older than API 1.42. Everything the process wrote is then one stream.
    /// </summary>
    private const string RawStreamContentType = "application/vnd.docker.raw-stream";

    /// <summary>
    /// How much of an exec's stderr is kept: the last 8 KiB. Diagnostics live at the end of a
    /// failure ("connection refused", the last psql error), while the front of a chatty tool's
    /// stderr is progress noise — and the whole point of streaming stdout past this process is not
    /// to hold the output of a database dump in memory.
    /// </summary>
    internal const int StderrTailBytes = 8 * 1024;

    /// <summary>
    /// Runs <paramref name="command"/> inside a running container (create + start + inspect, the
    /// API equivalent of <c>docker exec</c>) and returns once the process has exited. Stdout is
    /// written to <paramref name="stdout"/> as it arrives; a null <paramref name="stdout"/> drains
    /// the output instead, which is how a caller that only wants the exit code avoids buffering it.
    /// </summary>
    /// <param name="command">The argv to run — each element one token, no shell involved.</param>
    /// <param name="env">
    /// Extra environment for the process, in <c>KEY=VALUE</c> form. This is the channel secrets
    /// travel on (a database password as <c>PGPASSWORD</c>), so it is handed to the daemon and
    /// never logged, echoed into an exception, or kept in the result.
    /// </param>
    /// <param name="user">The user to run as, e.g. <c>postgres</c>; null keeps the image's default.</param>
    /// <exception cref="InvalidOperationException">
    /// The daemon reported no exit code once the output had ended — see the remarks.
    /// </exception>
    public async Task<DockerExecResult> ExecAsync(
        string containerId,
        IReadOnlyList<string> command,
        Stream? stdout = null,
        IReadOnlyList<string>? env = null,
        string? user = null,
        CancellationToken ct = default) {
        var body = new DockerCreateExecBody {
            AttachStdin = false,
            AttachStdout = true,
            AttachStderr = true,
            Tty = false,
            Cmd = [.. command],
            Env = env is { Count: > 0 } ? [.. env] : null,
            User = string.IsNullOrEmpty(user) ? null : user,
        };
        var json = JsonSerializer.Serialize(body, DockerJsonContext.Default.DockerCreateExecBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var createResponse = await _client.PostAsync($"{_apiBase}/containers/{containerId}/exec", content, ct);
        await EnsureSuccessWithBodyAsync(createResponse, ct);
        await using var createStream = await createResponse.Content.ReadAsStreamAsync(ct);
        var created = await JsonSerializer.DeserializeAsync(createStream, DockerJsonContext.Default.DockerCreateExecResponse, ct)
            ?? throw new InvalidOperationException($"Null response creating an exec in container {containerId}");

        var (stdoutBytes, stderr) = await RunExecAsync(created.Id, stdout, ct);

        var inspectResponse = await _client.GetAsync($"{_apiBase}/exec/{created.Id}/json", ct);
        await EnsureSuccessWithBodyAsync(inspectResponse, ct);
        await using var inspectStream = await inspectResponse.Content.ReadAsStreamAsync(ct);
        var inspect = await JsonSerializer.DeserializeAsync(inspectStream, DockerJsonContext.Default.DockerExecInspect, ct)
            ?? throw new InvalidOperationException($"Null response inspecting exec {created.Id}");
        // A missing exit code means the output stopped for a reason other than the process
        // finishing — the connection dropped, or the daemon restarted under it. The bytes already
        // written look like a complete result, so this has to be a failure rather than an exit 0.
        if (inspect.Running || inspect.ExitCode is not { } exitCode)
            throw new InvalidOperationException(
                $"The exec was still running when its output ended (container {containerId}), " +
                "so Docker reported no exit code — its output has to be assumed incomplete.");

        return new DockerExecResult(exitCode, stderr, stdoutBytes);
    }

    /// <summary>
    /// Starts a created exec and consumes its output, returning how many stdout bytes went past and
    /// the tail of stderr.
    /// </summary>
    /// <remarks>
    /// The one call on the untimed client that is also streamed. Headers arrive immediately, but
    /// HttpClient's ceiling keeps running while the body is read, and an exec that outlives it — a
    /// <c>pg_dumpall</c> of a real database takes minutes, not seconds — would have its output cut
    /// off mid-frame. Nothing downstream can tell that apart from output that simply ended, so the
    /// archive would quietly contain a short dump. The caller's token is the only bound here.
    /// </remarks>
    private async Task<(long StdoutBytes, string Stderr)> RunExecAsync(
        string execId, Stream? stdout, CancellationToken ct) {
        var json = JsonSerializer.Serialize(
            new DockerExecStartBody { Detach = false, Tty = false }, DockerJsonContext.Default.DockerExecStartBody);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_apiBase}/exec/{execId}/start") {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await _longRunningClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessWithBodyAsync(response, ct);

        var stderr = new ExecStderrTail(StderrTailBytes);
        long stdoutBytes = 0;
        await using var responseBody = await response.Content.ReadAsStreamAsync(ct);

        if (string.Equals(
                response.Content.Headers.ContentType?.MediaType, RawStreamContentType,
                StringComparison.OrdinalIgnoreCase)) {
            // Unframed: the body is the output verbatim, stderr included and indistinguishable.
            var buffer = new byte[8192];
            int read;
            while ((read = await responseBody.ReadAsync(buffer, ct)) > 0) {
                stdoutBytes += read;
                if (stdout is not null) await stdout.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            return (stdoutBytes, stderr.ToString());
        }

        await foreach (var frame in DockerStreamFrames.ReadAsync(responseBody, ct)) {
            if (frame.StreamType == DockerStreamFrame.Stderr) {
                stderr.Append(frame.Payload);
                continue;
            }
            stdoutBytes += frame.Payload.Length;
            if (stdout is not null) await stdout.WriteAsync(frame.Payload, ct);
        }
        return (stdoutBytes, stderr.ToString());
    }

    /// <summary>
    /// Keeps the last <paramref name="capacity"/> bytes written to it and renders them as text,
    /// prefixed with <c>…</c> once anything has been dropped.
    /// </summary>
    private sealed class ExecStderrTail(int capacity) {
        private readonly List<byte> _bytes = [];
        private bool _truncated;

        public void Append(byte[] payload) {
            _bytes.AddRange(payload);
            if (_bytes.Count <= capacity) return;
            _bytes.RemoveRange(0, _bytes.Count - capacity);
            _truncated = true;
        }

        public override string ToString() {
            if (_bytes.Count == 0) return string.Empty;
            var start = 0;
            // Cutting the front off may have landed inside a UTF-8 sequence; dropping the orphaned
            // continuation bytes costs at most three characters and saves a replacement glyph.
            if (_truncated)
                while (start < _bytes.Count && (_bytes[start] & 0xC0) == 0x80) start++;
            var text = Encoding.UTF8.GetString(_bytes.ToArray(), start, _bytes.Count - start);
            return _truncated ? $"…{text}" : text;
        }
    }

    /// <summary>Whether <paramref name="imageName"/> exists locally (<c>GET /images/{name}/json</c> → 404 = no).</summary>
    public async Task<bool> ImageExistsAsync(string imageName, CancellationToken ct = default) {
        var response = await _client.GetAsync($"{_apiBase}/images/{Uri.EscapeDataString(imageName)}/json", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    // ── Volumes ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Lists all volumes via <c>GET /volumes</c>. Docker wraps the array in an envelope
    /// <c>{ "Volumes": [...], "Warnings": [...] }</c> where <c>Volumes</c> may be null.
    /// Labels missing from the API are normalized to an empty dictionary.
    /// </summary>
    public async Task<IReadOnlyList<DockerVolumeInfo>> ListVolumesAsync(CancellationToken ct = default) {
        var response = await _client.GetAsync($"{_apiBase}/volumes", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStreamAsync(ct);
        var envelope = await JsonSerializer.DeserializeAsync(json, DockerJsonContext.Default.DockerVolumeListResponse, ct);
        var volumes = envelope?.Volumes;
        if (volumes is null || volumes.Count == 0) return [];
        // Normalize null labels to empty so callers never null-check the dictionary.
        return volumes
            .Select(v => v.Labels is null ? v with { Labels = [] } : v)
            .ToList();
    }

    /// <summary>
    /// Removes a single volume via <c>DELETE /volumes/{name}</c>. A non-success status
    /// (notably 409 = volume in use) throws <see cref="HttpRequestException"/> carrying the
    /// status code; callers surface the message.
    /// </summary>
    public async Task RemoveVolumeAsync(string name, CancellationToken ct = default) {
        var response = await _client.DeleteAsync($"{_apiBase}/volumes/{Uri.EscapeDataString(name)}", ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Returns a map of volume name → size in bytes from <c>GET /system/df</c>. Only volumes
    /// whose <c>UsageData.Size</c> is known (non-null and ≥ 0) are included; Docker reports
    /// <c>-1</c> or null for sizes it hasn't computed, which are treated as unknown and omitted.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, long>> GetVolumeSizesAsync(CancellationToken ct = default) {
        var df = await GetSystemDfAsync(ct);
        var sizes = new Dictionary<string, long>();
        foreach (var v in df.Volumes ?? []) {
            if (v.Name is null) continue;
            var size = v.UsageData?.Size;
            if (size is null or < 0) continue;
            sizes[v.Name] = size.Value;
        }
        return sizes;
    }

    /// <summary>
    /// Summarizes disk usage from <c>GET /system/df</c>: total image layers size plus the sum of
    /// container writable-layer sizes plus the sum of known volume sizes. Used as the docker-df
    /// disk fallback when host rootfs is unavailable.
    /// </summary>
    public async Task<DockerDfSummary> GetSystemDfSummaryAsync(CancellationToken ct = default) {
        var df = await GetSystemDfAsync(ct);
        var layersSize = df.LayersSize ?? 0;
        var containersSize = (df.Containers ?? []).Sum(c => c.SizeRw ?? 0);
        var volumesSize = (df.Volumes ?? [])
            .Select(v => v.UsageData?.Size ?? 0)
            .Where(s => s >= 0)
            .Sum();
        return new DockerDfSummary(layersSize, containersSize, volumesSize);
    }

    /// <summary>Single <c>GET /system/df</c> call shared by the volume-sizes and df-summary readers.</summary>
    private async Task<DockerSystemDfResponse> GetSystemDfAsync(CancellationToken ct) {
        var response = await _client.GetAsync($"{_apiBase}/system/df", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(json, DockerJsonContext.Default.DockerSystemDfResponse, ct)
            ?? new DockerSystemDfResponse();
    }

    // ── Networks ─────────────────────────────────────────────────────────────

    /// <summary>Lists all networks via <c>GET /networks</c>.</summary>
    public async Task<IReadOnlyList<DockerNetworkInfo>> ListNetworksAsync(CancellationToken ct = default) {
        var response = await _client.GetAsync($"{_apiBase}/networks", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(json, DockerJsonContext.Default.ListDockerNetworkInfo, ct)
            ?? [];
    }

    /// <summary>
    /// Inspects a single network via <c>GET /networks/{id}</c>, including its attached-container
    /// map. Container IPv4 addresses are returned by Docker in CIDR form (e.g. <c>172.18.0.4/16</c>);
    /// the mask suffix is stripped from <see cref="DockerNetworkContainer.IPv4Address"/> here.
    /// </summary>
    public async Task<DockerNetworkInfo> InspectNetworkAsync(string idOrName, CancellationToken ct = default) {
        var response = await _client.GetAsync($"{_apiBase}/networks/{Uri.EscapeDataString(idOrName)}", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStreamAsync(ct);
        var network = await JsonSerializer.DeserializeAsync(json, DockerJsonContext.Default.DockerNetworkInfo, ct)
            ?? throw new InvalidOperationException($"Null response inspecting network {idOrName}");
        if (network.Containers is null || network.Containers.Count == 0) return network;
        // Strip the CIDR mask so callers get a bare IP (172.18.0.4, not 172.18.0.4/16).
        var normalized = network.Containers.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value with { IPv4Address = StripCidr(kvp.Value.IPv4Address) });
        return network with { Containers = normalized };
    }

    /// <summary>Strips a trailing <c>/mask</c> from a CIDR address; returns null/empty unchanged.</summary>
    private static string? StripCidr(string? address) {
        if (string.IsNullOrEmpty(address)) return address;
        var slash = address.IndexOf('/');
        return slash < 0 ? address : address[..slash];
    }

    /// <summary>
    /// Creates a user-defined bridge network via <c>POST /networks/create</c> and returns its ID.
    /// Callers should check <see cref="ListNetworksAsync"/> first for idempotency — this method does
    /// not guard against duplicate names.
    /// </summary>
    public async Task<string> CreateNetworkAsync(
        string name, IReadOnlyDictionary<string, string>? labels = null, CancellationToken ct = default) {
        var body = new DockerCreateNetworkBody {
            Name = name,
            Driver = "bridge",
            Labels = labels is null ? null : new Dictionary<string, string>(labels),
        };
        var json = JsonSerializer.Serialize(body, DockerJsonContext.Default.DockerCreateNetworkBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync($"{_apiBase}/networks/create", content, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, DockerJsonContext.Default.DockerCreateNetworkResponse, ct)
            ?? throw new InvalidOperationException($"Null response creating network {name}");
        return result.Id;
    }

    /// <summary>
    /// Connects <paramref name="containerId"/> to a network via <c>POST /networks/{id}/connect</c>,
    /// optionally registering DNS <paramref name="aliases"/> so other containers on the network can
    /// resolve it by a stable name. A 403 ("endpoint already exists") is treated as success.
    /// </summary>
    public async Task ConnectContainerAsync(
        string networkIdOrName, string containerId, IReadOnlyList<string>? aliases = null, CancellationToken ct = default) {
        var body = new DockerConnectNetworkBody {
            Container = containerId,
            EndpointConfig = aliases is { Count: > 0 }
                ? new DockerEndpointConfig { Aliases = aliases.ToArray() }
                : null,
        };
        var json = JsonSerializer.Serialize(body, DockerJsonContext.Default.DockerConnectNetworkBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync(
            $"{_apiBase}/networks/{Uri.EscapeDataString(networkIdOrName)}/connect", content, ct);
        // 403 = endpoint already exists on this network; treat as already-connected.
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) return;
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Disconnects <paramref name="containerId"/> from a network via <c>POST /networks/{id}/disconnect</c>.</summary>
    public async Task DisconnectContainerAsync(
        string networkIdOrName, string containerId, bool force = false, CancellationToken ct = default) {
        var body = new DockerDisconnectNetworkBody { Container = containerId, Force = force };
        var json = JsonSerializer.Serialize(body, DockerJsonContext.Default.DockerDisconnectNetworkBody);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.PostAsync(
            $"{_apiBase}/networks/{Uri.EscapeDataString(networkIdOrName)}/disconnect", content, ct);
        // 404/403 = not connected / gone; treat as already-disconnected.
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden) return;
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Lists containers (all states) filtered by exact label matches, via
    /// <c>GET /containers/json?all=true&amp;filters={"label":["k=v",…]}</c>. Used to find a compose
    /// service's container(s) by <c>com.docker.compose.project</c> + <c>com.docker.compose.service</c>.
    /// </summary>
    public async Task<IReadOnlyList<DockerContainerInfo>> ListContainersByLabelsAsync(
        IReadOnlyList<string> labelFilters, CancellationToken ct = default) {
        var labelJson = JsonSerializer.Serialize(labelFilters.ToArray(), DockerJsonContext.Default.StringArray);
        var filters = $"{{\"label\":{labelJson}}}";
        var url = $"{_apiBase}/containers/json?all=true&filters={Uri.EscapeDataString(filters)}";
        var response = await _client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(json, DockerJsonContext.Default.ListDockerContainerInfo, ct)
            ?? [];
    }

    // ── Containers (all states) + stats ──────────────────────────────────────

    /// <summary>
    /// Returns all containers (running and stopped) via <c>GET /containers/json?all=true</c>.
    /// Needed for volume ref-counting, since stopped containers still hold volume references.
    /// Reuses the same <see cref="DockerContainerInfo"/> DTO as <see cref="ListContainersAsync"/>,
    /// which now includes each container's <c>Mounts</c>.
    /// </summary>
    public async Task<IReadOnlyList<DockerContainerInfo>> ListAllContainersAsync(CancellationToken ct = default) {
        var response = await _client.GetAsync($"{_apiBase}/containers/json?all=true", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(json, DockerJsonContext.Default.ListDockerContainerInfo, ct)
            ?? [];
    }

    /// <summary>
    /// Reads a single non-streaming stats snapshot for a container via
    /// <c>GET /containers/{id}/stats?stream=false&amp;one-shot=false</c>. Passing
    /// <c>one-shot=false</c> (the default) is required: <c>one-shot=true</c> omits
    /// <c>precpu_stats</c>, without which CPU% is not derivable from a single call.
    /// The raw counters are returned as-is — CPU% math is done by the sampler.
    /// </summary>
    public async Task<DockerContainerStats> GetContainerStatsAsync(string containerId, CancellationToken ct = default) {
        var response = await _client.GetAsync(
            $"{_apiBase}/containers/{containerId}/stats?stream=false&one-shot=false", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(json, DockerJsonContext.Default.DockerContainerStats, ct)
            ?? throw new InvalidOperationException($"Null stats response for container {containerId}");
    }

    /// <summary>
    /// Disposes both clients, and the handler only when this instance built it. Both clients are
    /// constructed with <c>disposeHandler: false</c>, so the handler has exactly one owner rather
    /// than being torn down twice — or torn out from under the second client by the first.
    /// </summary>
    public void Dispose() {
        _client.Dispose();
        _longRunningClient.Dispose();
        _ownedHandler?.Dispose();
    }

    // ── Self-update helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Returns the full container inspection record for <paramref name="containerId"/>.
    /// Use the HOSTNAME environment variable as the ID when inspecting the current container.
    /// </summary>
    public async Task<DockerContainerDetails> InspectContainerAsync(string containerId, CancellationToken ct = default) {
        var response = await _client.GetAsync($"{_apiBase}/containers/{containerId}/json", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(json, DockerJsonContext.Default.DockerContainerDetails, ct)
            ?? throw new InvalidOperationException($"Null response inspecting container {containerId}");
    }

    /// <summary>
    /// Returns the raw, untyped container inspection JSON. Used by the self-update coordinator to
    /// clone a container with full fidelity — the typed <see cref="DockerContainerDetails"/> model
    /// deliberately covers only the fields Watchtower reads, which would silently drop the rest of
    /// the configuration on a recreate.
    /// </summary>
    public async Task<System.Text.Json.Nodes.JsonObject> InspectContainerRawAsync(
        string containerId, CancellationToken ct = default) {
        var response = await _client.GetAsync($"{_apiBase}/containers/{containerId}/json", ct);
        response.EnsureSuccessStatusCode();
        await using var json = await response.Content.ReadAsStreamAsync(ct);
        var node = await System.Text.Json.Nodes.JsonNode.ParseAsync(json, cancellationToken: ct);
        return node?.AsObject()
            ?? throw new InvalidOperationException($"Null response inspecting container {containerId}");
    }

    /// <summary>
    /// Creates a container from a raw JSON body (Docker's create schema: Config fields at the top
    /// level plus <c>HostConfig</c>/<c>NetworkingConfig</c>) and returns its ID. Counterpart of
    /// <see cref="InspectContainerRawAsync"/> for full-fidelity recreates.
    /// </summary>
    public async Task<string> CreateContainerRawAsync(
        System.Text.Json.Nodes.JsonObject body, string name, CancellationToken ct = default) {
        var url = $"{_apiBase}/containers/create?name={Uri.EscapeDataString(name)}";
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await _client.PostAsync(url, content, ct);
        await EnsureSuccessWithBodyAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, DockerJsonContext.Default.DockerCreateContainerResponse, ct)
            ?? throw new InvalidOperationException("Null response creating container");
        return result.Id;
    }

    /// <summary>Renames a container. Works on running and stopped containers alike.</summary>
    public async Task RenameContainerAsync(string containerId, string newName, CancellationToken ct = default) {
        var response = await _client.PostAsync(
            $"{_apiBase}/containers/{containerId}/rename?name={Uri.EscapeDataString(newName)}", content: null, ct);
        await EnsureSuccessWithBodyAsync(response, ct);
    }

    /// <summary>
    /// Connects a container to a network with the given endpoint settings. Needed because the
    /// create endpoint accepts only a single network; additional ones are attached afterwards.
    /// </summary>
    public async Task ConnectNetworkAsync(
        string networkName, string containerId, System.Text.Json.Nodes.JsonObject endpointConfig,
        CancellationToken ct = default) {
        var body = new System.Text.Json.Nodes.JsonObject {
            ["Container"] = containerId,
            ["EndpointConfig"] = endpointConfig,
        };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await _client.PostAsync(
            $"{_apiBase}/networks/{Uri.EscapeDataString(networkName)}/connect", content, ct);
        await EnsureSuccessWithBodyAsync(response, ct);
    }

    /// <summary>
    /// Like <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/>, but includes the daemon's
    /// error message in the exception — the recreate path surfaces these to the user, and "409
    /// Conflict" alone is useless next to "name already in use by container …".
    /// </summary>
    private static async Task EnsureSuccessWithBodyAsync(HttpResponseMessage response, CancellationToken ct) {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Docker API returned {(int)response.StatusCode} {response.StatusCode}: {body.Trim()}",
            inner: null, response.StatusCode);
    }

    /// <summary>
    /// Pulls <paramref name="imageName"/> from the registry, optionally authenticating
    /// with <paramref name="username"/> and <paramref name="token"/>.
    /// Blocks until the pull stream is fully drained (i.e., the pull is complete).
    /// </summary>
    public async Task PullImageAsync(string imageName, string? username = null, string? token = null, CancellationToken ct = default) {
        // Parse the image reference into fromImage + tag for the query string.
        var lastColon = imageName.LastIndexOf(':');
        var lastSlash = imageName.LastIndexOf('/');
        string fromImage, tag;
        if (lastColon > lastSlash) {
            fromImage = imageName[..lastColon];
            tag = imageName[(lastColon + 1)..];
        } else {
            fromImage = imageName;
            tag = "latest";
        }

        var url = $"{_apiBase}/images/create?fromImage={Uri.EscapeDataString(fromImage)}&tag={Uri.EscapeDataString(tag)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);

        if (username is not null && token is not null) {
            // The X-Registry-Auth header carries a base64-encoded JSON auth object.
            var lastAt = fromImage.LastIndexOf('/');
            var serverAddress = lastAt > 0 ? fromImage[..lastAt] : "https://index.docker.io/v1/";
            var authJson = $"{{\"username\":\"{username}\",\"password\":\"{token}\",\"serveraddress\":\"{serverAddress}\"}}";
            request.Headers.Add("X-Registry-Auth", Convert.ToBase64String(Encoding.UTF8.GetBytes(authJson)));
        }

        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // Drain the streaming progress response so we wait for the pull to complete.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[4096];
        while (await stream.ReadAsync(buffer, ct) > 0) { /* drain */ }
    }

    /// <summary>
    /// Fetches the content digest of <paramref name="imageName"/> from the remote registry
    /// using a single <c>HEAD /v2/{name}/manifests/{reference}</c> request.
    /// No image layers are downloaded — this is typically 10–100× faster than a full pull.
    /// Returns the <c>Docker-Content-Digest</c> header value (sha256:…), or null when
    /// the registry does not support the OCI Distribution Spec manifest endpoint.
    /// </summary>
    public async Task<string?> GetRemoteDigestAsync(string imageName, string? username = null, string? token = null, CancellationToken ct = default) {
        // Parse registry host, repository path, and tag/digest reference.
        var lastColon = imageName.LastIndexOf(':');
        var lastSlash = imageName.LastIndexOf('/');
        string repository, reference;
        if (lastColon > lastSlash) {
            repository = imageName[..lastColon];
            reference = imageName[(lastColon + 1)..];
        } else {
            repository = imageName;
            reference = "latest";
        }

        // Split registry host from repository path. Docker Hub images without explicit
        // host (e.g. "myorg/watchtower") use index.docker.io as the registry host.
        string registryHost, repoPath;
        var firstSlash = repository.IndexOf('/');
        if (firstSlash > 0 && repository[..firstSlash].Contains('.')) {
            registryHost = repository[..firstSlash];
            repoPath = repository[(firstSlash + 1)..];
        } else {
            registryHost = "registry-1.docker.io";
            repoPath = repository.Contains('/') ? repository : $"library/{repository}";
        }

        var registryBase = $"https://{registryHost}";

        // Request both OCI and Docker manifest media types so the registry returns a digest.
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("Accept",
            "application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json, application/vnd.docker.distribution.manifest.list.v2+json");

        async Task<string?> TryHeadAsync(string? bearerToken) {
            using var req = new HttpRequestMessage(HttpMethod.Head,
                $"{registryBase}/v2/{repoPath}/manifests/{reference}");
            if (bearerToken is not null)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
            else if (username is not null && token is not null)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}")));

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) return null;
            return resp.Headers.TryGetValues("Docker-Content-Digest", out var vals)
                ? vals.FirstOrDefault()
                : null;
        }

        // First attempt (may return 401 with WWW-Authenticate for token auth).
        using var probe = new HttpRequestMessage(HttpMethod.Head,
            $"{registryBase}/v2/{repoPath}/manifests/{reference}");
        using var probeResp = await client.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);

        if (probeResp.StatusCode == System.Net.HttpStatusCode.Unauthorized) {
            var challenges = probeResp.Headers.WwwAuthenticate;

            // An htpasswd registry (`registry:2` with REGISTRY_AUTH=htpasswd) challenges with
            // `Basic realm="…"`, where the realm is a display string, not a token endpoint. The
            // stored credential answers it directly — there is no token to fetch.
            if (!challenges.Any(c => string.Equals(c.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))) {
                return challenges.Any(c => string.Equals(c.Scheme, "Basic", StringComparison.OrdinalIgnoreCase))
                    && username is not null && token is not null
                    ? await TryHeadAsync(bearerToken: null)
                    : null;
            }

            // Parse Bearer realm/service/scope from WWW-Authenticate header and fetch a token.
            var wwwAuth = challenges.First(
                c => string.Equals(c.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)).ToString();
            var realm   = ExtractParam(wwwAuth, "realm");
            var service = ExtractParam(wwwAuth, "service");
            var scope   = ExtractParam(wwwAuth, "scope");

            var tokenUrl = ResolveBearerTokenUrl(realm, service, scope, repoPath);
            if (tokenUrl is not null) {
                using var tokenReq = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
                if (username is not null && token is not null)
                    tokenReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}")));

                using var tokenResp = await client.SendAsync(tokenReq, ct);
                if (tokenResp.IsSuccessStatusCode) {
                    await using var stream = await tokenResp.Content.ReadAsStreamAsync(ct);
                    var tokenDoc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    var bearerToken = tokenDoc.RootElement.TryGetProperty("token", out var t) ? t.GetString()
                        : tokenDoc.RootElement.TryGetProperty("access_token", out var a) ? a.GetString()
                        : null;
                    return await TryHeadAsync(bearerToken);
                }
            }
        }

        if (probeResp.IsSuccessStatusCode)
            return probeResp.Headers.TryGetValues("Docker-Content-Digest", out var vals)
                ? vals.FirstOrDefault()
                : null;

        return null;
    }

    /// <summary>
    /// The token-endpoint URL for a Bearer challenge, or null when the challenge is unusable. A
    /// realm that is not an absolute http(s) URL cannot be dialled — handing it to HttpClient
    /// throws "An invalid request URI was provided" — so it is rejected here instead.
    /// </summary>
    internal static string? ResolveBearerTokenUrl(string? realm, string? service, string? scope, string repoPath) {
        if (realm is null
            || !Uri.TryCreate(realm, UriKind.Absolute, out var realmUri)
            || (realmUri.Scheme != Uri.UriSchemeHttp && realmUri.Scheme != Uri.UriSchemeHttps))
            return null;
        return $"{realm}?service={Uri.EscapeDataString(service ?? string.Empty)}"
            + $"&scope={Uri.EscapeDataString(scope ?? $"repository:{repoPath}:pull")}";
    }

    private static string? ExtractParam(string header, string key) {
        var search = $"{key}=\"";
        var start = header.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += search.Length;
        var end = header.IndexOf('"', start);
        return end < 0 ? null : header[start..end];
    }

    /// <summary>
    /// Removes dangling (untagged) images via <c>POST /images/prune</c> — the API equivalent of
    /// <c>docker image prune -f</c>. Returns what the daemon deleted and how many bytes it reclaimed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately never the <c>-a</c> ("all unused") variant: that also deletes tagged images no
    /// container currently runs — the images a <c>docker compose up</c> without a pull reuses.
    /// </para>
    /// <para>
    /// Goes through the untimed client, because the daemon withholds the response headers until the
    /// prune has finished and a backlog of layers can take longer than the 100-second default. That
    /// leaves cancellation as the only bound, so the caller's token is linked with
    /// <see cref="PruneTimeout"/> — this runs from a background loop, which must not park forever on
    /// a daemon that never answers.
    /// </para>
    /// </remarks>
    /// <exception cref="TimeoutException">
    /// The prune outlasted <see cref="PruneTimeout"/>. Deliberately not an
    /// <see cref="OperationCanceledException"/>: callers treat that as "we are shutting down" and
    /// stay quiet about it, and hitting the ceiling is the opposite of routine.
    /// </exception>
    public async Task<DockerPruneImagesResponse> PruneImagesAsync(CancellationToken ct = default) {
        // The cap gets its own source rather than a CancelAfter on the linked one, so "the cap fired"
        // is a fact to read off `cap` instead of something inferred from the caller's token: a
        // shutdown arriving right behind an expired cap must not turn the timeout back into a
        // cancellation the caller then swallows as routine.
        using var cap = new CancellationTokenSource(_pruneTimeout);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, cap.Token);
        try {
            var response = await _longRunningClient.PostAsync(BuildImagePruneUrl(_apiBase), content: null, bounded.Token);
            // The prune runs unattended with no UI surface, so the daemon's message is the only
            // diagnostic there will ever be — e.g. a socket mounted read-only answers 403 with a body.
            await EnsureSuccessWithBodyAsync(response, bounded.Token);
            var json = await response.Content.ReadAsStreamAsync(bounded.Token);
            return await JsonSerializer.DeserializeAsync(json, DockerJsonContext.Default.DockerPruneImagesResponse, bounded.Token)
                ?? new DockerPruneImagesResponse();
        } catch (OperationCanceledException) when (cap.IsCancellationRequested) {
            throw new TimeoutException(
                $"The dangling-image prune exceeded the client-side cap of {_pruneTimeout}. " +
                "The daemon may still be carrying it through.");
        }
    }

    /// <summary>
    /// Builds the prune URL: <c>POST /images/prune?filters={"dangling":["true"]}</c>. Docker takes
    /// prune filters as a JSON object in the <c>filters</c> query parameter (same encoding as the
    /// label filter in <see cref="ListContainersByLabelsAsync"/>), not as a request body.
    /// <c>dangling=true</c> is sent explicitly rather than relying on the endpoint's default, because
    /// the opposite value (<c>dangling=false</c>) is what <c>docker image prune -a</c> sends and the
    /// difference between the two is every tagged image on the host.
    /// </summary>
    internal static string BuildImagePruneUrl(string apiBase) {
        const string filters = """{"dangling":["true"]}""";
        return $"{apiBase}/images/prune?filters={Uri.EscapeDataString(filters)}";
    }

    /// <summary>The image must already be present locally (i.e., pulled first).</summary>
    public async Task<DockerImageInfo> InspectImageAsync(string imageName, CancellationToken ct = default) {
        var response = await _client.GetAsync($"{_apiBase}/images/{Uri.EscapeDataString(imageName)}/json", ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(json, DockerJsonContext.Default.DockerImageInfo, ct)
            ?? throw new InvalidOperationException($"Null response inspecting image {imageName}");
    }
}

/// <summary>
/// Subset of fields from the Docker Engine API GET /containers/json response.
/// Docker returns PascalCase, matched case-insensitively by <see cref="DockerJsonContext"/>.
/// </summary>
public sealed record DockerContainerInfo {
    public required string Id { get; init; }
    public required string[] Names { get; init; }
    public required string Image { get; init; }
    /// <summary>
    /// The <c>sha256:…</c> id of the image the container is actually running (Docker's <c>ImageID</c>).
    /// <see cref="Image"/> is only the reference it was started from, which keeps pointing at the tag
    /// after a newer image is pulled under it — this is what tells "pulled" from "pulled and recreated"
    /// apart. Despite the non-nullable type this can arrive null (the daemon sending an explicit null
    /// overwrites the initializer below), so treat it as unset rather than dereferencing it.
    /// </summary>
    public string ImageId { get; init; } = string.Empty;
    public required string State { get; init; }
    public required string Status { get; init; }
    public required Dictionary<string, string> Labels { get; init; }
    /// <summary>
    /// Mounts attached to the container, as returned by GET /containers/json (all states).
    /// Named-volume mounts have <c>Type == "volume"</c> and a non-empty <c>Name</c>; the
    /// Volumes module intersects these against the volume list to compute ref-counts / inUseBy.
    /// May be null/empty for containers with no mounts.
    /// </summary>
    public DockerMountInfo[] Mounts { get; init; } = [];
    /// <summary>
    /// Published/exposed port bindings, as returned by GET /containers/json. Each entry may or may
    /// not carry a <c>PublicPort</c>/<c>IP</c> (an exposed-but-unpublished port has neither). The
    /// Networks module derives the exposure map and host-port conflicts from these. May be
    /// null/empty for containers that publish no ports.
    /// </summary>
    public DockerPortInfo[] Ports { get; init; } = [];
}

/// <summary>A single entry from a container's <c>Ports</c> array (GET /containers/json).</summary>
public sealed record DockerPortInfo {
    /// <summary>Host bind IP (e.g. "0.0.0.0", "127.0.0.1", "::"); null/empty when unpublished.</summary>
    public string? IP { get; init; }
    /// <summary>Container-side port.</summary>
    public int PrivatePort { get; init; }
    /// <summary>Host-side port; null when the port is exposed but not published.</summary>
    public int? PublicPort { get; init; }
    /// <summary>"tcp" or "udp".</summary>
    public string Type { get; init; } = "tcp";
}

/// <summary>A single entry from a container's <c>Mounts</c> array (GET /containers/json).</summary>
public sealed record DockerMountInfo {
    /// <summary>"volume", "bind", "tmpfs", etc. Named volumes are "volume".</summary>
    public string Type { get; init; } = "";
    /// <summary>Volume name for <c>Type == "volume"</c>; empty for anonymous/bind mounts.</summary>
    public string Name { get; init; } = "";
    /// <summary>Source on the host (mountpoint for volumes, host path for binds).</summary>
    public string Source { get; init; } = "";
    /// <summary>Mount path inside the container.</summary>
    public string Destination { get; init; } = "";
    /// <summary>True when the mount is read-write.</summary>
    public bool RW { get; init; }
}

/// <summary>
/// Outcome of one <see cref="DockerEngineClient.ExecAsync"/> call. Stdout is not in here — it went
/// to the caller's stream as it arrived; only how much of it there was is reported back.
/// </summary>
/// <param name="ExitCode">The process's exit status, as Docker reports it after the exec ended.</param>
/// <param name="Stderr">
/// The tail of what the process wrote to stderr (at most
/// <see cref="DockerEngineClient.StderrTailBytes"/> bytes, prefixed with <c>…</c> when the front
/// was dropped). Empty for an unframed exec, whose stderr is mixed into stdout by the daemon.
/// </param>
/// <param name="StdoutBytes">Number of stdout bytes seen, whether or not they were written anywhere.</param>
public sealed record DockerExecResult(int ExitCode, string Stderr, long StdoutBytes) {
    /// <summary>True when the process exited 0.</summary>
    public bool Success => ExitCode == 0;
}

/// <summary>Request body for POST /containers/{id}/exec.</summary>
public sealed record DockerCreateExecBody {
    /// <summary>Always false — Watchtower never writes to an exec, which is what keeps the start
    /// response a plain body instead of a hijacked bidirectional connection.</summary>
    public bool AttachStdin { get; init; }
    public bool AttachStdout { get; init; }
    public bool AttachStderr { get; init; }
    /// <summary>Always false: a TTY would merge stderr into stdout and lose the framing.</summary>
    public bool Tty { get; init; }
    /// <summary>The command to run — each element is a separate argv token.</summary>
    public required string[] Cmd { get; init; }
    /// <summary>Extra environment in "KEY=VALUE" form; null when the caller passed none.</summary>
    public string[]? Env { get; init; }
    /// <summary>User to run as; null keeps the image's default.</summary>
    public string? User { get; init; }
}

/// <summary>Response body from POST /containers/{id}/exec.</summary>
public sealed record DockerCreateExecResponse {
    public required string Id { get; init; }
}

/// <summary>Request body for POST /exec/{id}/start.</summary>
public sealed record DockerExecStartBody {
    /// <summary>False, so the response body carries the output and the call ends with the process.</summary>
    public bool Detach { get; init; }
    /// <summary>Mirrors <see cref="DockerCreateExecBody.Tty"/>; both must agree.</summary>
    public bool Tty { get; init; }
}

/// <summary>Subset of GET /exec/{id}/json: the exit status of a finished exec.</summary>
public sealed record DockerExecInspect {
    /// <summary>True while the process is still going, in which case there is no exit code yet.</summary>
    public bool Running { get; init; }
    /// <summary>Null while the exec is running; set once it has ended.</summary>
    public int? ExitCode { get; init; }
}

/// <summary>Response body from POST /containers/{id}/wait.</summary>
public sealed record DockerWaitContainerResponse {
    public required int StatusCode { get; init; }
}

/// <summary>
/// Response body from POST /images/prune: <c>{ "ImagesDeleted": [...], "SpaceReclaimed": 1234 }</c>.
/// Docker returns <c>ImagesDeleted: null</c> (not an empty array) when nothing was dangling.
/// </summary>
public sealed record DockerPruneImagesResponse {
    /// <summary>One entry per untagged/removed layer; null when the daemon deleted nothing.</summary>
    public List<DockerDeletedImage>? ImagesDeleted { get; init; }

    /// <summary>Bytes of disk the prune freed.</summary>
    public long SpaceReclaimed { get; init; }

    /// <summary>Number of entries in <see cref="ImagesDeleted"/>, treating null as zero.</summary>
    public int DeletedCount => ImagesDeleted?.Count ?? 0;
}

/// <summary>
/// A single entry of the prune response's <c>ImagesDeleted</c> array. Exactly one of the two
/// properties is populated per entry: <c>Untagged</c> when a reference was removed, <c>Deleted</c>
/// when the layer itself went away.
/// </summary>
public sealed record DockerDeletedImage {
    public string? Untagged { get; init; }
    public string? Deleted { get; init; }
}

/// <summary>
/// STJ source-generation context for Docker Engine API types.
/// Separate from the module JSON contexts because Docker uses PascalCase.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<DockerContainerInfo>))]
[JsonSerializable(typeof(DockerContainerInfo))]
[JsonSerializable(typeof(DockerMountInfo))]
[JsonSerializable(typeof(DockerPortInfo))]
[JsonSerializable(typeof(DockerContainerDetails))]
[JsonSerializable(typeof(DockerContainerConfig))]
[JsonSerializable(typeof(DockerContainerState))]
[JsonSerializable(typeof(DockerImageInfo))]
[JsonSerializable(typeof(DockerCreateContainerBody))]
[JsonSerializable(typeof(DockerCreateContainerResponse))]
[JsonSerializable(typeof(DockerEmptyObject))]
[JsonSerializable(typeof(DockerPortBinding))]
[JsonSerializable(typeof(DockerRestartPolicy))]
[JsonSerializable(typeof(DockerNetworkingConfig))]
[JsonSerializable(typeof(DockerEndpointConfig))]
[JsonSerializable(typeof(DockerCreateNetworkBody))]
[JsonSerializable(typeof(DockerCreateNetworkResponse))]
[JsonSerializable(typeof(DockerConnectNetworkBody))]
[JsonSerializable(typeof(DockerDisconnectNetworkBody))]
[JsonSerializable(typeof(DockerCreateExecBody))]
[JsonSerializable(typeof(DockerCreateExecResponse))]
[JsonSerializable(typeof(DockerExecStartBody))]
[JsonSerializable(typeof(DockerExecInspect))]
[JsonSerializable(typeof(DockerWaitContainerResponse))]
[JsonSerializable(typeof(DockerPruneImagesResponse))]
[JsonSerializable(typeof(DockerDeletedImage))]
[JsonSerializable(typeof(DockerVolumeListResponse))]
[JsonSerializable(typeof(DockerVolumeInfo))]
[JsonSerializable(typeof(DockerSystemDfResponse))]
[JsonSerializable(typeof(DockerDfVolume))]
[JsonSerializable(typeof(DockerDfVolumeUsage))]
[JsonSerializable(typeof(DockerDfContainer))]
[JsonSerializable(typeof(List<DockerNetworkInfo>))]
[JsonSerializable(typeof(DockerNetworkInfo))]
[JsonSerializable(typeof(DockerNetworkIpam))]
[JsonSerializable(typeof(DockerNetworkIpamConfig))]
[JsonSerializable(typeof(DockerNetworkContainer))]
[JsonSerializable(typeof(DockerContainerStats))]
[JsonSerializable(typeof(DockerCpuStats))]
[JsonSerializable(typeof(DockerCpuUsage))]
[JsonSerializable(typeof(DockerMemoryStats))]
[JsonSerializable(typeof(DockerMemoryStatsDetail))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class DockerJsonContext : JsonSerializerContext;

// ── Volumes DTOs ─────────────────────────────────────────────────────────────

/// <summary>Envelope from GET /volumes: <c>{ "Volumes": [...], "Warnings": [...] }</c>.</summary>
public sealed record DockerVolumeListResponse {
    /// <summary>May be null when the daemon returns no volumes.</summary>
    public List<DockerVolumeInfo>? Volumes { get; init; }
    public string[]? Warnings { get; init; }
}

/// <summary>A single volume from GET /volumes.</summary>
public sealed record DockerVolumeInfo {
    public required string Name { get; init; }
    public string Driver { get; init; } = "";
    public string Mountpoint { get; init; } = "";
    /// <summary>ISO-8601 creation timestamp. Present on the list response.</summary>
    public string? CreatedAt { get; init; }
    /// <summary>Null in the API when the volume has no labels; normalized to empty by the client.</summary>
    public Dictionary<string, string>? Labels { get; init; }
    public string Scope { get; init; } = "";
}

// ── /system/df DTOs ──────────────────────────────────────────────────────────

/// <summary>Subset of GET /system/df used for volume sizes and the disk-usage summary.</summary>
public sealed record DockerSystemDfResponse {
    /// <summary>Total size of all image layers, in bytes. Null when not reported.</summary>
    public long? LayersSize { get; init; }
    public List<DockerDfContainer>? Containers { get; init; }
    public List<DockerDfVolume>? Volumes { get; init; }
}

/// <summary>A container entry in GET /system/df (only the writable-layer size is read).</summary>
public sealed record DockerDfContainer {
    /// <summary>Size of the container's writable layer in bytes. Null when not computed.</summary>
    public long? SizeRw { get; init; }
}

/// <summary>A volume entry in GET /system/df.</summary>
public sealed record DockerDfVolume {
    public string? Name { get; init; }
    public DockerDfVolumeUsage? UsageData { get; init; }
}

/// <summary>Usage block for a df volume entry.</summary>
public sealed record DockerDfVolumeUsage {
    /// <summary>Volume size in bytes; Docker reports <c>-1</c> (or null) when unknown.</summary>
    public long? Size { get; init; }
    /// <summary>Number of containers referencing the volume; <c>-1</c> when unknown.</summary>
    public long? RefCount { get; init; }
}

// ── Networks DTOs ────────────────────────────────────────────────────────────

/// <summary>A network from GET /networks (list) or GET /networks/{id} (inspect, with Containers).</summary>
public sealed record DockerNetworkInfo {
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Driver { get; init; } = "";
    public string Scope { get; init; } = "";
    public bool Internal { get; init; }
    /// <summary>Creation timestamp — the API field is "Created" (not "CreatedAt").</summary>
    [JsonPropertyName("Created")]
    public string? CreatedAt { get; init; }
    public Dictionary<string, string>? Labels { get; init; }
    public DockerNetworkIpam? IPAM { get; init; }
    /// <summary>
    /// Attached containers keyed by container ID. Populated only by the inspect endpoint
    /// (GET /networks/{id}); the list endpoint returns this empty.
    /// </summary>
    public Dictionary<string, DockerNetworkContainer>? Containers { get; init; }
}

/// <summary>IPAM block of a network; the first Config entry carries subnet + gateway.</summary>
public sealed record DockerNetworkIpam {
    public List<DockerNetworkIpamConfig>? Config { get; init; }
}

/// <summary>One IPAM config entry (subnet + gateway).</summary>
public sealed record DockerNetworkIpamConfig {
    public string? Subnet { get; init; }
    public string? Gateway { get; init; }
}

/// <summary>An attached container in a network inspect response.</summary>
public sealed record DockerNetworkContainer {
    public string? Name { get; init; }
    /// <summary>CIDR form ("172.18.0.4/16") from the API; the client strips the mask.</summary>
    public string? IPv4Address { get; init; }
    public string? IPv6Address { get; init; }
}

// ── Container stats DTOs (snake_case fields → explicit JsonPropertyName) ──────

/// <summary>
/// Subset of GET /containers/{id}/stats?stream=false. Exposes the raw counters needed to
/// derive CPU% and real memory usage; the actual math is done by the metrics sampler.
/// </summary>
public sealed record DockerContainerStats {
    [JsonPropertyName("cpu_stats")]
    public DockerCpuStats? CpuStats { get; init; }
    [JsonPropertyName("precpu_stats")]
    public DockerCpuStats? PreCpuStats { get; init; }
    [JsonPropertyName("memory_stats")]
    public DockerMemoryStats? MemoryStats { get; init; }
}

/// <summary>cpu_stats / precpu_stats block.</summary>
public sealed record DockerCpuStats {
    [JsonPropertyName("cpu_usage")]
    public DockerCpuUsage? CpuUsage { get; init; }
    [JsonPropertyName("system_cpu_usage")]
    public ulong? SystemCpuUsage { get; init; }
    [JsonPropertyName("online_cpus")]
    public int? OnlineCpus { get; init; }
}

/// <summary>cpu_usage sub-block.</summary>
public sealed record DockerCpuUsage {
    [JsonPropertyName("total_usage")]
    public ulong TotalUsage { get; init; }
}

/// <summary>memory_stats block. Real usage is <c>usage - stats.inactive_file</c> (guard missing).</summary>
public sealed record DockerMemoryStats {
    [JsonPropertyName("usage")]
    public ulong? Usage { get; init; }
    [JsonPropertyName("limit")]
    public ulong? Limit { get; init; }
    [JsonPropertyName("stats")]
    public DockerMemoryStatsDetail? Stats { get; init; }
}

/// <summary>memory_stats.stats sub-block (only inactive_file is read, cgroup v1/v2 name).</summary>
public sealed record DockerMemoryStatsDetail {
    [JsonPropertyName("inactive_file")]
    public ulong? InactiveFile { get; init; }
}

/// <summary>Aggregated disk-usage summary derived from GET /system/df.</summary>
public sealed record DockerDfSummary(long LayersSize, long ContainersSizeRw, long VolumesSize);

/// <summary>
/// Subset of the Docker Engine API GET /containers/{id}/json response.
/// Used to retrieve the sha256 image ID and compose labels of a running container.
/// </summary>
public sealed record DockerContainerDetails {
    public required string Id { get; init; }
    // Docker Engine API returns the image SHA as "Image" at the container root level,
    // not "ImageID". Config.Image (inside the nested Config block) holds the image name/tag.
    [JsonPropertyName("Image")]
    public required string ImageID { get; init; }
    /// <summary>Container configuration including the image name and labels.</summary>
    public DockerContainerConfig Config { get; init; } = new() { Image = "" };
    /// <summary>Runtime state (status, exit code). Populated by the inspect endpoint.</summary>
    public DockerContainerState? State { get; init; }
}

/// <summary>Runtime state fields from the Docker container inspect response.</summary>
public sealed record DockerContainerState {
    /// <summary>Container status string: "created", "running", "paused", "restarting", "removing", "exited", "dead".</summary>
    public string Status { get; init; } = "";
    /// <summary>Exit code of the container process (0 = success). Only meaningful when Status is "exited".</summary>
    public int ExitCode { get; init; }
}

/// <summary>Subset of the Docker container Config block returned by the inspect API.</summary>
public sealed record DockerContainerConfig {
    /// <summary>The image name/tag used to start the container (e.g. "ghcr.io/org/app:latest").</summary>
    public required string Image { get; init; }
    /// <summary>Environment variables in "KEY=VALUE" form; null when the API omits them.</summary>
    public string[]? Env { get; init; }
    /// <summary>
    /// Container labels. For Compose-managed containers this includes
    /// <c>com.docker.compose.project</c> and <c>com.docker.compose.project.config_files</c>.
    /// </summary>
    public Dictionary<string, string> Labels { get; init; } = [];
}

/// <summary>
/// Subset of the Docker Engine API GET /images/{name}/json response.
/// Used to compare the locally pulled image ID against the running container's image.
/// </summary>
public sealed record DockerImageInfo {
    /// <summary>sha256 digest of the local image layer content.</summary>
    public required string Id { get; init; }
    public string[] RepoTags { get; init; } = [];
    public string[] RepoDigests { get; init; } = [];
}

/// <summary>Request body for POST /containers/create.</summary>
public sealed record DockerCreateContainerBody {
    public required string Image { get; init; }
    /// <summary>Command to run — each element is a separate argv token.</summary>
    public string[]? Cmd { get; init; }
    /// <summary>Environment variables in "KEY=VALUE" format.</summary>
    public string[]? Env { get; init; }
    /// <summary>User (name or uid, optionally ":group") to run the container as; null keeps the image default.</summary>
    public string? User { get; init; }
    /// <summary>Container labels (e.g. an ownership marker so Watchtower can find its managed containers).</summary>
    public Dictionary<string, string>? Labels { get; init; }
    /// <summary>Ports the container exposes, keyed "443/tcp"; each value is an empty object.</summary>
    public Dictionary<string, DockerEmptyObject>? ExposedPorts { get; init; }
    public DockerCreateHostConfig? HostConfig { get; init; }
    /// <summary>
    /// Network attachment at create time. At most one network may be specified here; attach further
    /// networks afterwards with <see cref="DockerEngineClient.ConnectContainerAsync"/>.
    /// </summary>
    public DockerNetworkingConfig? NetworkingConfig { get; init; }
}

/// <summary>HostConfig fields used when creating the coordinator and Caddy containers.</summary>
public sealed record DockerCreateHostConfig {
    /// <summary>Bind mounts in the form "host-path:container-path[:options]". Named volumes work here too
    /// (e.g. "caddy_data:/data") and are auto-created by the daemon if missing.</summary>
    public string[]? Binds { get; init; }
    /// <summary>When true the container is automatically removed when it exits.</summary>
    public bool AutoRemove { get; init; }
    /// <summary>Network mode for the container (e.g. "none", "host").</summary>
    public string? NetworkMode { get; init; }
    /// <summary>
    /// Additional group IDs (as strings) to add to the container's process.
    /// Used to grant the coordinator the same supplemental GIDs as the main container,
    /// ensuring identical Docker socket access permissions.
    /// </summary>
    public string[]? GroupAdd { get; init; }
    /// <summary>Host port publishing, keyed "443/tcp" → list of host bindings.</summary>
    public Dictionary<string, List<DockerPortBinding>>? PortBindings { get; init; }
    /// <summary>Restart policy (e.g. Name = "unless-stopped") for long-lived managed containers.</summary>
    public DockerRestartPolicy? RestartPolicy { get; init; }
}

/// <summary>Response body from POST /containers/create.</summary>
public sealed record DockerCreateContainerResponse {
    public required string Id { get; init; }
}

/// <summary>An empty JSON object (<c>{}</c>). Used as the value type of <c>ExposedPorts</c>.</summary>
public sealed record DockerEmptyObject;

/// <summary>A single host port binding for <c>HostConfig.PortBindings</c>.</summary>
public sealed record DockerPortBinding {
    /// <summary>Host bind IP (e.g. "0.0.0.0"); null lets Docker pick the default.</summary>
    public string? HostIp { get; init; }
    /// <summary>Host port as a string, e.g. "443".</summary>
    public required string HostPort { get; init; }
}

/// <summary>Container restart policy (e.g. Name = "unless-stopped", "always", "on-failure").</summary>
public sealed record DockerRestartPolicy {
    public required string Name { get; init; }
    public int MaximumRetryCount { get; init; }
}

/// <summary>NetworkingConfig for POST /containers/create: endpoints keyed by network name.</summary>
public sealed record DockerNetworkingConfig {
    public Dictionary<string, DockerEndpointConfig>? EndpointsConfig { get; init; }
}

/// <summary>Per-network endpoint settings (DNS aliases) for connect / create.</summary>
public sealed record DockerEndpointConfig {
    public string[]? Aliases { get; init; }
}

/// <summary>Request body for POST /networks/create.</summary>
public sealed record DockerCreateNetworkBody {
    public required string Name { get; init; }
    public string Driver { get; init; } = "bridge";
    public Dictionary<string, string>? Labels { get; init; }
}

/// <summary>Response body from POST /networks/create.</summary>
public sealed record DockerCreateNetworkResponse {
    public required string Id { get; init; }
    public string? Warning { get; init; }
}

/// <summary>Request body for POST /networks/{id}/connect.</summary>
public sealed record DockerConnectNetworkBody {
    public required string Container { get; init; }
    public DockerEndpointConfig? EndpointConfig { get; init; }
}

/// <summary>Request body for POST /networks/{id}/disconnect.</summary>
public sealed record DockerDisconnectNetworkBody {
    public required string Container { get; init; }
    public bool Force { get; init; }
}
