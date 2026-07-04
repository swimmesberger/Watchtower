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
[JsonSerializable(typeof(DockerMountInfo))]
[JsonSerializable(typeof(DockerPortInfo))]
[JsonSerializable(typeof(DockerContainerDetails))]
[JsonSerializable(typeof(DockerContainerConfig))]
[JsonSerializable(typeof(DockerContainerState))]
[JsonSerializable(typeof(DockerImageInfo))]
[JsonSerializable(typeof(DockerCreateContainerBody))]
[JsonSerializable(typeof(DockerCreateContainerResponse))]
[JsonSerializable(typeof(DockerWaitContainerResponse))]
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
