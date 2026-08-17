using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Services;

/// <summary>
/// Minimal Cloudflare API v4 client for the Cloudflare Tunnel proxy provider (ADR-0015): find/create
/// the remotely-managed tunnel, fetch its run token, replace its ingress configuration, and upsert the
/// proxied CNAME records the routes need. Stateless singleton; the API token is passed per call (it
/// comes from the proxy settings) and never leaves this process.
/// </summary>
/// <remarks>
/// Follows the <see cref="GitHubApiClient"/> pattern: hand-rolled <see cref="HttpClient"/>,
/// source-generated JSON, errors surfaced as <see cref="HttpRequestException"/> carrying Cloudflare's
/// own error messages so reconcile logs say what the API actually objected to.
/// </remarks>
public sealed class CloudflareApiClient : IDisposable {
    private readonly HttpClient _client;

    public CloudflareApiClient() {
        _client = new HttpClient { BaseAddress = new Uri("https://api.cloudflare.com/client/v4/") };
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("watchtower-proxy", "1.0"));
    }

    /// <summary>Finds a non-deleted tunnel by exact name; null when none exists.</summary>
    public async Task<CloudflareTunnel?> FindTunnelAsync(string accountId, string name, string token, CancellationToken ct = default) {
        var url = $"accounts/{accountId}/cfd_tunnel?name={Uri.EscapeDataString(name)}&is_deleted=false";
        var result = await SendAsync(HttpMethod.Get, url, token, body: null,
            CloudflareJsonContext.Default.CloudflareEnvelopeListCloudflareTunnel, ct);
        return result.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.Ordinal));
    }

    /// <summary>Creates a remotely-managed tunnel (<c>config_src: cloudflare</c>) so ingress lives in the API.</summary>
    public async Task<CloudflareTunnel> CreateTunnelAsync(string accountId, string name, string token, CancellationToken ct = default) {
        var body = JsonSerializer.Serialize(
            new CloudflareCreateTunnelRequest { Name = name, ConfigSrc = "cloudflare" },
            CloudflareJsonContext.Default.CloudflareCreateTunnelRequest);
        return await SendAsync(HttpMethod.Post, $"accounts/{accountId}/cfd_tunnel", token, body,
            CloudflareJsonContext.Default.CloudflareEnvelopeCloudflareTunnel, ct);
    }

    /// <summary>The token a cloudflared instance uses to run this tunnel (<c>cloudflared tunnel run --token …</c>).</summary>
    public async Task<string> GetTunnelTokenAsync(string accountId, string tunnelId, string token, CancellationToken ct = default) {
        return await SendAsync(HttpMethod.Get, $"accounts/{accountId}/cfd_tunnel/{tunnelId}/token", token, body: null,
            CloudflareJsonContext.Default.CloudflareEnvelopeString, ct);
    }

    /// <summary>
    /// Replaces the tunnel's remote configuration with the given ingress rules (whole-set put — the
    /// route table is the source of truth, exactly like the Caddyfile regeneration).
    /// </summary>
    public async Task PutTunnelConfigurationAsync(
        string accountId, string tunnelId, IReadOnlyList<CloudflareIngressRule> ingress, string token,
        CancellationToken ct = default) {
        var body = JsonSerializer.Serialize(
            new CloudflarePutConfigurationRequest { Config = new CloudflareTunnelConfig { Ingress = ingress.ToArray() } },
            CloudflareJsonContext.Default.CloudflarePutConfigurationRequest);
        await SendAsync(HttpMethod.Put, $"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations", token, body,
            CloudflareJsonContext.Default.CloudflareEnvelopeJsonElement, ct);
    }

    /// <summary>DNS records in the zone with this exact name (any type), for the CNAME upsert.</summary>
    public async Task<IReadOnlyList<CloudflareDnsRecord>> ListDnsRecordsAsync(
        string zoneId, string name, string token, CancellationToken ct = default) {
        var url = $"zones/{zoneId}/dns_records?name={Uri.EscapeDataString(name)}";
        return await SendAsync(HttpMethod.Get, url, token, body: null,
            CloudflareJsonContext.Default.CloudflareEnvelopeListCloudflareDnsRecord, ct);
    }

    /// <summary>Creates or updates the proxied CNAME <paramref name="name"/> → <paramref name="target"/>.</summary>
    public async Task UpsertDnsCnameAsync(string zoneId, string name, string target, string token, CancellationToken ct = default) {
        var existing = (await ListDnsRecordsAsync(zoneId, name, token, ct))
            .FirstOrDefault(r => string.Equals(r.Type, "CNAME", StringComparison.OrdinalIgnoreCase));
        var record = JsonSerializer.Serialize(
            new CloudflareDnsRecordRequest { Type = "CNAME", Name = name, Content = target, Proxied = true, Ttl = 1 },
            CloudflareJsonContext.Default.CloudflareDnsRecordRequest);
        if (existing is null) {
            await SendAsync(HttpMethod.Post, $"zones/{zoneId}/dns_records", token, record,
                CloudflareJsonContext.Default.CloudflareEnvelopeJsonElement, ct);
        } else if (!string.Equals(existing.Content, target, StringComparison.OrdinalIgnoreCase) || existing.Proxied != true) {
            await SendAsync(HttpMethod.Put, $"zones/{zoneId}/dns_records/{existing.Id}", token, record,
                CloudflareJsonContext.Default.CloudflareEnvelopeJsonElement, ct);
        }
    }

    /// <summary>
    /// Cheap credential probe for the settings surface: verifies the token can read the account's
    /// tunnels. Returns null on success, else a human-readable reason.
    /// </summary>
    public async Task<string?> ValidateAccessAsync(string accountId, string token, CancellationToken ct = default) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"accounts/{accountId}/cfd_tunnel?per_page=1");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await _client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(ct);
            return $"Cloudflare API {(int)response.StatusCode}: {(text.Length > 200 ? text[..200] : text)}";
        } catch (Exception ex) {
            return $"Cloudflare API unreachable: {ex.Message}";
        }
    }

    private async Task<T> SendAsync<T>(
        HttpMethod method, string url, string token, string? body,
        JsonTypeInfo<CloudflareEnvelope<T>> typeInfo, CancellationToken ct) {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        CloudflareEnvelope<T>? envelope = null;
        try {
            envelope = JsonSerializer.Deserialize(text, typeInfo);
        } catch (JsonException) {
            // Fall through to the status-based error below with the raw body snippet.
        }
        if (!response.IsSuccessStatusCode || envelope is not { Success: true }) {
            var reason = envelope?.Errors is { Length: > 0 } errors
                ? string.Join("; ", errors.Select(e => $"{e.Code}: {e.Message}"))
                : text.Length > 300 ? text[..300] : text;
            throw new HttpRequestException($"Cloudflare API {(int)response.StatusCode} on {method} {url}: {reason}");
        }
        return envelope.Result is null
            ? throw new HttpRequestException($"Cloudflare API returned success with a null result on {method} {url}.")
            : envelope.Result;
    }

    public void Dispose() => _client.Dispose();
}

// ── Wire types (Cloudflare API v4) ───────────────────────────────────────────

/// <summary>The standard Cloudflare v4 response envelope.</summary>
public sealed record CloudflareEnvelope<T> {
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("errors")] public CloudflareApiError[]? Errors { get; init; }
    [JsonPropertyName("result")] public T? Result { get; init; }
}

public sealed record CloudflareApiError {
    [JsonPropertyName("code")] public long Code { get; init; }
    [JsonPropertyName("message")] public string Message { get; init; } = "";
}

public sealed record CloudflareTunnel {
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
}

public sealed record CloudflareCreateTunnelRequest {
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("config_src")] public required string ConfigSrc { get; init; }
}

/// <summary>One tunnel ingress rule: requests for <see cref="Hostname"/> go to <see cref="Service"/>.
/// The final rule must be a catch-all (<c>Hostname</c> null, e.g. <c>http_status:404</c>).</summary>
public sealed record CloudflareIngressRule {
    [JsonPropertyName("hostname")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hostname { get; init; }

    [JsonPropertyName("service")] public required string Service { get; init; }
}

public sealed record CloudflareTunnelConfig {
    [JsonPropertyName("ingress")] public required CloudflareIngressRule[] Ingress { get; init; }
}

public sealed record CloudflarePutConfigurationRequest {
    [JsonPropertyName("config")] public required CloudflareTunnelConfig Config { get; init; }
}

public sealed record CloudflareDnsRecord {
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("content")] public string Content { get; init; } = "";
    [JsonPropertyName("proxied")] public bool? Proxied { get; init; }
}

public sealed record CloudflareDnsRecordRequest {
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("content")] public required string Content { get; init; }
    [JsonPropertyName("proxied")] public required bool Proxied { get; init; }
    /// <summary>1 = automatic TTL (required for proxied records).</summary>
    [JsonPropertyName("ttl")] public required int Ttl { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CloudflareEnvelope<CloudflareTunnel>))]
[JsonSerializable(typeof(CloudflareEnvelope<List<CloudflareTunnel>>))]
[JsonSerializable(typeof(CloudflareEnvelope<string>))]
[JsonSerializable(typeof(CloudflareEnvelope<List<CloudflareDnsRecord>>))]
[JsonSerializable(typeof(CloudflareEnvelope<JsonElement>))]
[JsonSerializable(typeof(CloudflareCreateTunnelRequest))]
[JsonSerializable(typeof(CloudflarePutConfigurationRequest))]
[JsonSerializable(typeof(CloudflareDnsRecordRequest))]
internal sealed partial class CloudflareJsonContext : JsonSerializerContext;
