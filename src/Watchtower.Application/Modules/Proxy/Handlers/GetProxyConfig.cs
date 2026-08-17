using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Returns the reverse-proxy configuration for the Settings page: enablement, the selected provider
/// (ADR-0015), the Caddy values, and the Cloudflare Tunnel values — with the API token reduced to a
/// has-a-value flag, because it is a secret and the UI only needs to know whether one is stored.
/// Everything here is runtime-switchable: the providers watch the options and start, stop or
/// reconfigure their data plane when they change — no restart. Env-pinned paths ride along so the UI
/// can disable those fields (env wins over the settings store).
/// </summary>
[Handler("proxy.getConfig")]
public sealed class GetProxyConfig(IOptionsMonitor<WatchtowerOptions> options, EnvironmentSettingPins pins)
    : IHandler<GetProxyConfig.Query, Result<GetProxyConfig.Response>> {
    public sealed record Query;

    public sealed record Response(ProxyConfigDto Config);

    /// <summary>Every path the proxy card manages — shared with <see cref="UpdateProxyConfig"/>.</summary>
    internal static readonly string[] ProxyPaths = [
        WatchtowerSettingPaths.ProxyEnabled,
        WatchtowerSettingPaths.ProxyProvider,
        WatchtowerSettingPaths.ProxyAdminEmail,
        WatchtowerSettingPaths.ProxyCaddyImage,
        WatchtowerSettingPaths.ProxyCloudflareAccountId,
        WatchtowerSettingPaths.ProxyCloudflareZoneId,
        WatchtowerSettingPaths.ProxyCloudflareApiToken,
        WatchtowerSettingPaths.ProxyCloudflareTunnelName,
        WatchtowerSettingPaths.ProxyCloudflareManaged,
        WatchtowerSettingPaths.ProxyCloudflareCloudflaredImage,
        WatchtowerSettingPaths.ProxyCloudflareCloudflaredContainerName,
        WatchtowerSettingPaths.ProxyCloudflareAccessAllowedEmails,
        WatchtowerSettingPaths.ProxyCloudflareAccessAllowedEmailDomains,
    ];

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var proxy = options.CurrentValue.Proxy;
        return ValueTask.FromResult<Result<Response>>(new Response(ProxyConfigDto.From(proxy, pins)));
    }
}

/// <summary>The proxy configuration surfaced to the Settings page (token reduced to a flag).</summary>
public sealed record ProxyConfigDto(
    bool Enabled,
    string Provider,
    string? AdminEmail,
    string CaddyImage,
    ProxyCloudflareConfigDto Cloudflare,
    string[] PinnedPaths) {
    internal static ProxyConfigDto From(ProxyOptions proxy, EnvironmentSettingPins pins) => new(
        Enabled: proxy.Enabled,
        Provider: proxy.ResolveProvider() == ProxyProviderKind.Cloudflare ? "cloudflare" : "caddy",
        AdminEmail: proxy.AdminEmail,
        CaddyImage: proxy.CaddyImage,
        Cloudflare: new ProxyCloudflareConfigDto(
            AccountId: proxy.Cloudflare.AccountId,
            ZoneId: proxy.Cloudflare.ZoneId,
            HasApiToken: !string.IsNullOrWhiteSpace(proxy.Cloudflare.ApiToken),
            TunnelName: proxy.Cloudflare.TunnelName,
            Managed: proxy.Cloudflare.Managed,
            CloudflaredImage: proxy.Cloudflare.CloudflaredImage,
            CloudflaredContainerName: proxy.Cloudflare.CloudflaredContainerName,
            AccessAllowedEmails: proxy.Cloudflare.AccessAllowedEmails,
            AccessAllowedEmailDomains: proxy.Cloudflare.AccessAllowedEmailDomains),
        PinnedPaths: pins.Pinned(GetProxyConfig.ProxyPaths));
}

/// <summary>Cloudflare Tunnel connection values for the config surface (token reduced to a flag).</summary>
public sealed record ProxyCloudflareConfigDto(
    string? AccountId,
    string? ZoneId,
    bool HasApiToken,
    string TunnelName,
    bool Managed,
    string CloudflaredImage,
    string? CloudflaredContainerName,
    string AccessAllowedEmails,
    string AccessAllowedEmailDomains);
