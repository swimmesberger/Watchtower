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
/// Uses a persistent HttpClient configured with a custom SocketsHttpHandler so no
/// real TCP connection is made — the socket path is passed as the "host" in requests.
/// </summary>
public sealed class DockerEngineClient : IDisposable {
    private readonly HttpClient _client;
    private readonly string _apiBase;

    /// <param name="options">
    /// Watchtower options — reads <c>DockerApiVersion</c> to build the API base path
    /// (e.g. <c>/v1.43</c>). This is the same version used by <see cref="ComposeCliService"/>
    /// via <c>DOCKER_API_VERSION</c>, ensuring both communicate with the daemon at the same level.
    /// </param>
    public DockerEngineClient(IOptions<WatchtowerOptions> options) {
        _apiBase = $"/v{options.Value.DockerApiVersion}";
        var handler = new SocketsHttpHandler {
            // Route all HTTP requests through the Docker Unix domain socket.
            ConnectCallback = async (ctx, ct) => {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint("/var/run/docker.sock"), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        // The hostname is ignored when using a Unix socket; "docker" is used for clarity in logs.
        _client = new HttpClient(handler) { BaseAddress = new Uri("http://docker") };
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

    /// <summary>Sends a stop signal to the specified container (SIGTERM → SIGKILL after 10s).</summary>
    public async Task StopContainerAsync(string containerId, CancellationToken ct = default) {
        var response = await _client.PostAsync($"{_apiBase}/containers/{containerId}/stop", content: null, ct);
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
        var header = new byte[8];

        while (!ct.IsCancellationRequested) {
            // Each Docker log frame begins with an 8-byte header.
            var bytesRead = await ReadExactAsync(stream, header, 8, ct);
            if (bytesRead < 8) yield break;

            // Bytes 4–7 encode the frame payload size as a big-endian uint32.
            var frameSize =
                (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
            if (frameSize == 0) continue;

            var frameBuffer = new byte[frameSize];
            bytesRead = await ReadExactAsync(stream, frameBuffer, frameSize, ct);
            if (bytesRead < frameSize) yield break;

            var text = Encoding.UTF8.GetString(frameBuffer).TrimEnd('\n', '\r');
            if (!string.IsNullOrEmpty(text)) yield return text;
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes into <paramref name="buffer"/>.
    /// Returns the number of bytes read (may be less than count on EOF).
    /// </summary>
    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct) {
        var offset = 0;
        while (offset < count) {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0) break; // EOF
            offset += read;
        }
        return offset;
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
    public async Task<int> WaitContainerAsync(string containerId, CancellationToken ct = default) {
        var response = await _client.PostAsync($"{_apiBase}/containers/{containerId}/wait?condition=not-running", content: null, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, DockerJsonContext.Default.DockerWaitContainerResponse, ct)
            ?? throw new InvalidOperationException("Null response waiting for container");
        return result.StatusCode;
    }

    public void Dispose() => _client.Dispose();

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
            // Parse Bearer realm/service/scope from WWW-Authenticate header and fetch a token.
            var wwwAuth = probeResp.Headers.WwwAuthenticate.FirstOrDefault()?.ToString() ?? string.Empty;
            var realm   = ExtractParam(wwwAuth, "realm");
            var service = ExtractParam(wwwAuth, "service");
            var scope   = ExtractParam(wwwAuth, "scope");

            if (realm is not null) {
                var tokenUrl = $"{realm}?service={Uri.EscapeDataString(service ?? string.Empty)}&scope={Uri.EscapeDataString(scope ?? $"repository:{repoPath}:pull")}";
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

    private static string? ExtractParam(string header, string key) {
        var search = $"{key}=\"";
        var start = header.IndexOf(search, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        start += search.Length;
        var end = header.IndexOf('"', start);
        return end < 0 ? null : header[start..end];
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
    public required string State { get; init; }
    public required string Status { get; init; }
    public required Dictionary<string, string> Labels { get; init; }
}

/// <summary>Response body from POST /containers/{id}/wait.</summary>
public sealed record DockerWaitContainerResponse {
    public required int StatusCode { get; init; }
}

/// <summary>
/// STJ source-generation context for Docker Engine API types.
/// Separate from the module JSON contexts because Docker uses PascalCase.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(List<DockerContainerInfo>))]
[JsonSerializable(typeof(DockerContainerInfo))]
[JsonSerializable(typeof(DockerContainerDetails))]
[JsonSerializable(typeof(DockerContainerConfig))]
[JsonSerializable(typeof(DockerContainerState))]
[JsonSerializable(typeof(DockerImageInfo))]
[JsonSerializable(typeof(DockerCreateContainerBody))]
[JsonSerializable(typeof(DockerCreateContainerResponse))]
[JsonSerializable(typeof(DockerWaitContainerResponse))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class DockerJsonContext : JsonSerializerContext;

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
    public DockerCreateHostConfig? HostConfig { get; init; }
}

/// <summary>HostConfig fields used when creating the coordinator container.</summary>
public sealed record DockerCreateHostConfig {
    /// <summary>Bind mounts in the form "host-path:container-path[:options]".</summary>
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
}

/// <summary>Response body from POST /containers/create.</summary>
public sealed record DockerCreateContainerResponse {
    public required string Id { get; init; }
}
