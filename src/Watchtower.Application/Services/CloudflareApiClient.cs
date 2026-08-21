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

    /// <summary>All non-deleted tunnels in the account — foreign-hostname discovery reads every one,
    /// because pre-existing applications typically live on a tunnel Watchtower did not create.</summary>
    public async Task<IReadOnlyList<CloudflareTunnel>> ListTunnelsAsync(
        string accountId, string token, CancellationToken ct = default) {
        return await SendAsync(HttpMethod.Get, $"accounts/{accountId}/cfd_tunnel?is_deleted=false&per_page=100",
            token, body: null, CloudflareJsonContext.Default.CloudflareEnvelopeListCloudflareTunnel, ct);
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

    /// <summary>
    /// The tunnel's current remote configuration. Returns an empty rule list for a tunnel that has no
    /// configuration yet (fresh tunnel, or one still locally managed by a config file).
    /// </summary>
    public async Task<IReadOnlyList<CloudflareIngressRule>> GetTunnelConfigurationAsync(
        string accountId, string tunnelId, string token, CancellationToken ct = default) {
        var result = await SendAsync(HttpMethod.Get, $"accounts/{accountId}/cfd_tunnel/{tunnelId}/configurations",
            token, body: null, CloudflareJsonContext.Default.CloudflareEnvelopeCloudflareTunnelConfigurationResult, ct);
        return result.Config?.Ingress ?? [];
    }

    /// <summary>DNS records in the zone with this exact name (any type), for the CNAME upsert.</summary>
    public async Task<IReadOnlyList<CloudflareDnsRecord>> ListDnsRecordsAsync(
        string zoneId, string name, string token, CancellationToken ct = default) {
        var url = $"zones/{zoneId}/dns_records?name={Uri.EscapeDataString(name)}";
        return await SendAsync(HttpMethod.Get, url, token, body: null,
            CloudflareJsonContext.Default.CloudflareEnvelopeListCloudflareDnsRecord, ct);
    }

    /// <summary>
    /// Creates or updates the proxied CNAME <paramref name="name"/> → <paramref name="target"/>.
    /// Reports what actually happened so the caller's audit trail records writes, not intentions.
    /// </summary>
    public async Task<CloudflareDnsUpsert> UpsertDnsCnameAsync(string zoneId, string name, string target, string token, CancellationToken ct = default) {
        var existing = (await ListDnsRecordsAsync(zoneId, name, token, ct))
            .FirstOrDefault(r => string.Equals(r.Type, "CNAME", StringComparison.OrdinalIgnoreCase));
        var record = JsonSerializer.Serialize(
            new CloudflareDnsRecordRequest { Type = "CNAME", Name = name, Content = target, Proxied = true, Ttl = 1 },
            CloudflareJsonContext.Default.CloudflareDnsRecordRequest);
        if (existing is null) {
            await SendAsync(HttpMethod.Post, $"zones/{zoneId}/dns_records", token, record,
                CloudflareJsonContext.Default.CloudflareEnvelopeJsonElement, ct);
            return CloudflareDnsUpsert.Created;
        }
        if (!string.Equals(existing.Content, target, StringComparison.OrdinalIgnoreCase) || existing.Proxied != true) {
            await SendAsync(HttpMethod.Put, $"zones/{zoneId}/dns_records/{existing.Id}", token, record,
                CloudflareJsonContext.Default.CloudflareEnvelopeJsonElement, ct);
            return CloudflareDnsUpsert.Updated;
        }
        return CloudflareDnsUpsert.Unchanged;
    }

    // ── Zero Trust Access (phase 3 of ADR-0015) ──────────────────────────────

    /// <summary>The account's Access applications (first 100 — plenty for a single-node deployment).</summary>
    public async Task<IReadOnlyList<CloudflareAccessApp>> ListAccessAppsAsync(
        string accountId, string token, CancellationToken ct = default) {
        return await SendAsync(HttpMethod.Get, $"accounts/{accountId}/access/apps?per_page=100", token, body: null,
            CloudflareJsonContext.Default.CloudflareEnvelopeListCloudflareAccessApp, ct);
    }

    /// <summary>Creates a <c>self_hosted</c> Access application for one hostname.</summary>
    public async Task<CloudflareAccessApp> CreateAccessAppAsync(
        string accountId, CloudflareAccessAppRequest app, string token, CancellationToken ct = default) {
        var body = JsonSerializer.Serialize(app, CloudflareJsonContext.Default.CloudflareAccessAppRequest);
        return await SendAsync(HttpMethod.Post, $"accounts/{accountId}/access/apps", token, body,
            CloudflareJsonContext.Default.CloudflareEnvelopeCloudflareAccessApp, ct);
    }

    /// <summary>Updates an Access application in place (same id, refreshed name/domain/session).</summary>
    public async Task<CloudflareAccessApp> UpdateAccessAppAsync(
        string accountId, string appId, CloudflareAccessAppRequest app, string token, CancellationToken ct = default) {
        var body = JsonSerializer.Serialize(app, CloudflareJsonContext.Default.CloudflareAccessAppRequest);
        return await SendAsync(HttpMethod.Put, $"accounts/{accountId}/access/apps/{appId}", token, body,
            CloudflareJsonContext.Default.CloudflareEnvelopeCloudflareAccessApp, ct);
    }

    /// <summary>Deletes an Access application (used only on apps carrying the Watchtower name prefix).</summary>
    public async Task DeleteAccessAppAsync(string accountId, string appId, string token, CancellationToken ct = default) {
        await SendAsync(HttpMethod.Delete, $"accounts/{accountId}/access/apps/{appId}", token, body: null,
            CloudflareJsonContext.Default.CloudflareEnvelopeJsonElement, ct);
    }

    /// <summary>The app-scoped policies of one Access application.</summary>
    public async Task<IReadOnlyList<CloudflareAccessPolicy>> ListAccessPoliciesAsync(
        string accountId, string appId, string token, CancellationToken ct = default) {
        return await SendAsync(HttpMethod.Get, $"accounts/{accountId}/access/apps/{appId}/policies?per_page=100",
            token, body: null, CloudflareJsonContext.Default.CloudflareEnvelopeListCloudflareAccessPolicy, ct);
    }

    /// <summary>Creates an app-scoped allow policy.</summary>
    public async Task CreateAccessPolicyAsync(
        string accountId, string appId, CloudflareAccessPolicyRequest policy, string token, CancellationToken ct = default) {
        var body = JsonSerializer.Serialize(policy, CloudflareJsonContext.Default.CloudflareAccessPolicyRequest);
        await SendAsync(HttpMethod.Post, $"accounts/{accountId}/access/apps/{appId}/policies", token, body,
            CloudflareJsonContext.Default.CloudflareEnvelopeJsonElement, ct);
    }

    /// <summary>Replaces an app-scoped policy's rules.</summary>
    public async Task UpdateAccessPolicyAsync(
        string accountId, string appId, string policyId, CloudflareAccessPolicyRequest policy, string token,
        CancellationToken ct = default) {
        var body = JsonSerializer.Serialize(policy, CloudflareJsonContext.Default.CloudflareAccessPolicyRequest);
        await SendAsync(HttpMethod.Put, $"accounts/{accountId}/access/apps/{appId}/policies/{policyId}", token, body,
            CloudflareJsonContext.Default.CloudflareEnvelopeJsonElement, ct);
    }

    /// <summary>Deletes an app-scoped policy (used when the Watchtower-generated rule set becomes empty).</summary>
    public async Task DeleteAccessPolicyAsync(
        string accountId, string appId, string policyId, string token, CancellationToken ct = default) {
        await SendAsync(HttpMethod.Delete, $"accounts/{accountId}/access/apps/{appId}/policies/{policyId}", token,
            body: null, CloudflareJsonContext.Default.CloudflareEnvelopeJsonElement, ct);
    }

    /// <summary>
    /// Cheap credential probe for the settings surface: verifies the token can read the account's
    /// tunnels. Returns null on success, else a human-readable reason.
    /// </summary>
    public async Task<string?> ValidateAccessAsync(string accountId, string token, CancellationToken ct = default) =>
        await ProbeAsync($"accounts/{accountId}/cfd_tunnel?per_page=1", token, ct);

    /// <summary>
    /// Probes the zone the routes' DNS records live in. A token with tunnel permissions but without
    /// <c>Zone → DNS → Edit</c> on this zone passes <see cref="ValidateAccessAsync"/> and then fails every
    /// CNAME upsert with <c>10000: Authentication error</c> in a reconcile nobody is watching — this is
    /// the same failure surfaced at save time, with Cloudflare's own words. Null when readable.
    /// </summary>
    public async Task<string?> ValidateZoneAccessAsync(string zoneId, string token, CancellationToken ct = default) =>
        await ProbeAsync($"zones/{zoneId}/dns_records?per_page=1", token, ct);

    private async Task<string?> ProbeAsync(string url, string token, CancellationToken ct) {
        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await _client.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return null;
            var text = await response.Content.ReadAsStringAsync(ct);
            return $"Cloudflare API {(int)response.StatusCode} on GET {url}: {(text.Length > 200 ? text[..200] : text)}";
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

/// <summary>What a DNS upsert actually did — the audit trail records writes, not intentions.</summary>
public enum CloudflareDnsUpsert {
    Created,
    Updated,
    Unchanged,
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

/// <summary>One tunnel ingress rule: requests for <see cref="Hostname"/> (optionally narrowed by
/// <see cref="Path"/>) go to <see cref="Service"/>. The final rule must be a catch-all
/// (<c>Hostname</c> null, e.g. <c>http_status:404</c>).</summary>
public sealed record CloudflareIngressRule {
    [JsonPropertyName("hostname")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hostname { get; init; }

    [JsonPropertyName("service")] public required string Service { get; init; }

    /// <summary>Optional path filter. Watchtower never writes one, but foreign (dashboard-made) rules
    /// may carry it, and the merge must round-trip it untouched.</summary>
    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }
}

public sealed record CloudflareTunnelConfig {
    [JsonPropertyName("ingress")] public required CloudflareIngressRule[] Ingress { get; init; }
}

public sealed record CloudflarePutConfigurationRequest {
    [JsonPropertyName("config")] public required CloudflareTunnelConfig Config { get; init; }
}

/// <summary>Read side of the configurations endpoint — everything optional, because a fresh tunnel
/// (or one still driven by a local config file) reports no remote configuration at all.</summary>
public sealed record CloudflareTunnelConfigurationResult {
    [JsonPropertyName("config")] public CloudflareTunnelConfigRead? Config { get; init; }
}

public sealed record CloudflareTunnelConfigRead {
    [JsonPropertyName("ingress")] public CloudflareIngressRule[]? Ingress { get; init; }
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

/// <summary>A Zero Trust Access application (only the fields the reconcile reads).</summary>
public sealed record CloudflareAccessApp {
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("domain")] public string Domain { get; init; } = "";
    [JsonPropertyName("type")] public string Type { get; init; } = "";
}

public sealed record CloudflareAccessAppRequest {
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("domain")] public required string Domain { get; init; }
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("session_duration")] public required string SessionDuration { get; init; }
    [JsonPropertyName("app_launcher_visible")] public required bool AppLauncherVisible { get; init; }

    /// <summary>
    /// Reusable Access policy ids to attach to the app. Null omits the field entirely so an update
    /// leaves existing attachments untouched; a non-empty array replaces them.
    /// </summary>
    [JsonPropertyName("policies")] public string[]? Policies { get; init; }
}

public sealed record CloudflareAccessPolicy {
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("decision")] public string Decision { get; init; } = "";
}

public sealed record CloudflareAccessPolicyRequest {
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("decision")] public required string Decision { get; init; }
    [JsonPropertyName("include")] public required CloudflareAccessRule[] Include { get; init; }
    [JsonPropertyName("precedence")] public required int Precedence { get; init; }
}

/// <summary>
/// One Access include rule. Exactly one member is set; the others stay null and are omitted from the
/// JSON — Cloudflare's rule objects are single-key discriminated unions.
/// </summary>
public sealed record CloudflareAccessRule {
    [JsonPropertyName("email")] public CloudflareEmailRule? Email { get; init; }
    [JsonPropertyName("email_domain")] public CloudflareEmailDomainRule? EmailDomain { get; init; }
    [JsonPropertyName("group")] public CloudflareGroupRule? Group { get; init; }

    public static CloudflareAccessRule ForEmail(string email) => new() { Email = new CloudflareEmailRule { Email = email } };
    public static CloudflareAccessRule ForEmailDomain(string domain) => new() { EmailDomain = new CloudflareEmailDomainRule { Domain = domain } };
    public static CloudflareAccessRule ForGroup(string groupId) => new() { Group = new CloudflareGroupRule { Id = groupId } };
}

public sealed record CloudflareEmailRule {
    [JsonPropertyName("email")] public required string Email { get; init; }
}

public sealed record CloudflareEmailDomainRule {
    [JsonPropertyName("domain")] public required string Domain { get; init; }
}

/// <summary>References a Zero Trust Access group by id — the "main user group" workflow.</summary>
public sealed record CloudflareGroupRule {
    [JsonPropertyName("id")] public required string Id { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CloudflareEnvelope<CloudflareTunnel>))]
[JsonSerializable(typeof(CloudflareEnvelope<List<CloudflareTunnel>>))]
[JsonSerializable(typeof(CloudflareEnvelope<string>))]
[JsonSerializable(typeof(CloudflareEnvelope<List<CloudflareDnsRecord>>))]
[JsonSerializable(typeof(CloudflareEnvelope<JsonElement>))]
[JsonSerializable(typeof(CloudflareEnvelope<CloudflareTunnelConfigurationResult>))]
[JsonSerializable(typeof(CloudflareEnvelope<CloudflareAccessApp>))]
[JsonSerializable(typeof(CloudflareEnvelope<List<CloudflareAccessApp>>))]
[JsonSerializable(typeof(CloudflareEnvelope<List<CloudflareAccessPolicy>>))]
[JsonSerializable(typeof(CloudflareCreateTunnelRequest))]
[JsonSerializable(typeof(CloudflarePutConfigurationRequest))]
[JsonSerializable(typeof(CloudflareDnsRecordRequest))]
[JsonSerializable(typeof(CloudflareAccessAppRequest))]
[JsonSerializable(typeof(CloudflareAccessPolicyRequest))]
internal sealed partial class CloudflareJsonContext : JsonSerializerContext;
