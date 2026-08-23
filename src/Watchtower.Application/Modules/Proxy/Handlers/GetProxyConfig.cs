using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Returns the reverse-proxy configuration for the Settings page: enablement, the selected provider
/// (ADR-0015), the Caddy values, the in-process proxy's ACME values, and the Cloudflare Tunnel values
/// — with every secret reduced to a has-a-value flag, because the UI only needs to know whether one is
/// stored. Everything here is runtime-switchable: the providers watch the options and start, stop or
/// reconfigure their data plane when they change — no restart. Env-pinned paths ride along so the UI
/// can disable those fields (env wins over the settings store).
/// </summary>
[Handler("proxy.getConfig")]
public sealed class GetProxyConfig(
    IOptionsMonitor<WatchtowerOptions> options, EnvironmentSettingPins pins, YarpListenerState listener)
    : IHandler<GetProxyConfig.Query, Result<GetProxyConfig.Response>> {
    public sealed record Query;

    public sealed record Response(ProxyConfigDto Config);

    /// <summary>Every path the proxy card manages — shared with <see cref="UpdateProxyConfig"/>.</summary>
    internal static readonly string[] ProxyPaths = [
        WatchtowerSettingPaths.ProxyEnabled,
        WatchtowerSettingPaths.ProxyProvider,
        WatchtowerSettingPaths.ProxyAdminEmail,
        WatchtowerSettingPaths.ProxyCaddyImage,
        WatchtowerSettingPaths.ProxyYarpAcmeDirectoryUrl,
        WatchtowerSettingPaths.ProxyYarpAcmeCaBundlePath,
        WatchtowerSettingPaths.ProxyYarpAcmeEabKeyId,
        WatchtowerSettingPaths.ProxyYarpAcmeEabHmacKey,
        WatchtowerSettingPaths.ProxyYarpRedirectHttpToHttps,
        WatchtowerSettingPaths.ProxyCloudflareAccountId,
        WatchtowerSettingPaths.ProxyCloudflareZoneId,
        WatchtowerSettingPaths.ProxyCloudflareApiToken,
        WatchtowerSettingPaths.ProxyCloudflareTunnelName,
        WatchtowerSettingPaths.ProxyCloudflareTeamDomain,
        WatchtowerSettingPaths.ProxyCloudflareManaged,
        WatchtowerSettingPaths.ProxyCloudflareCloudflaredImage,
        WatchtowerSettingPaths.ProxyCloudflareCloudflaredContainerName,
        WatchtowerSettingPaths.ProxyCloudflareAccessAllowedEmails,
        WatchtowerSettingPaths.ProxyCloudflareAccessAllowedEmailDomains,
        WatchtowerSettingPaths.ProxyCloudflareAccessGroupIds,
        WatchtowerSettingPaths.ProxyCloudflareAccessReusablePolicyIds,
    ];

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var proxy = options.CurrentValue.Proxy;
        return ValueTask.FromResult<Result<Response>>(
            new Response(ProxyConfigDto.From(proxy, pins, listener.HttpsBound)));
    }
}

/// <summary>The proxy configuration surfaced to the Settings page (secrets reduced to flags).</summary>
public sealed record ProxyConfigDto(
    bool Enabled,
    string Provider,
    string? AdminEmail,
    string CaddyImage,
    ProxyYarpConfigDto Yarp,
    ProxyCloudflareConfigDto Cloudflare,
    string[] PinnedPaths) {
    internal static ProxyConfigDto From(
        ProxyOptions proxy, EnvironmentSettingPins pins, bool httpsListenerBound) => new(
        Enabled: proxy.Enabled,
        Provider: proxy.ProviderName(),
        AdminEmail: proxy.AdminEmail,
        CaddyImage: proxy.CaddyImage,
        Yarp: new ProxyYarpConfigDto(
            AcmeDirectoryUrl: proxy.Yarp.AcmeDirectoryUrl,
            AcmeCaBundlePath: proxy.Yarp.AcmeCaBundlePath,
            AcmeEabKeyId: proxy.Yarp.AcmeEabKeyId,
            HasAcmeEabHmacKey: !string.IsNullOrWhiteSpace(proxy.Yarp.AcmeEabHmacKey),
            RedirectHttpToHttps: proxy.Yarp.RedirectHttpToHttps,
            HttpsListenerBound: httpsListenerBound),
        Cloudflare: new ProxyCloudflareConfigDto(
            AccountId: proxy.Cloudflare.AccountId,
            ZoneId: proxy.Cloudflare.ZoneId,
            HasApiToken: !string.IsNullOrWhiteSpace(proxy.Cloudflare.ApiToken),
            TunnelName: proxy.Cloudflare.TunnelName,
            TeamDomain: proxy.Cloudflare.TeamDomain,
            Managed: proxy.Cloudflare.Managed,
            CloudflaredImage: proxy.Cloudflare.CloudflaredImage,
            CloudflaredContainerName: proxy.Cloudflare.CloudflaredContainerName,
            AccessAllowedEmails: proxy.Cloudflare.AccessAllowedEmails,
            AccessAllowedEmailDomains: proxy.Cloudflare.AccessAllowedEmailDomains,
            AccessGroupIds: proxy.Cloudflare.AccessGroupIds,
            AccessReusablePolicyIds: proxy.Cloudflare.AccessReusablePolicyIds),
        PinnedPaths: pins.Pinned(GetProxyConfig.ProxyPaths));
}

/// <summary>
/// In-process proxy values for the config surface. The EAB HMAC key is a secret and appears only as
/// <paramref name="HasAcmeEabHmacKey"/>. <paramref name="HttpsListenerBound"/> is runtime state rather
/// than configuration — it is what lets the Settings page say "enabled, but 443 never came up" instead
/// of looking healthy while every route is served over plain HTTP.
/// </summary>
/// <remarks>
/// There is no certificate directory any more: certificates are rows (ADR-0024), so the field the card
/// used to show — a read-only path an operator could do nothing with — has no counterpart to replace it.
/// </remarks>
public sealed record ProxyYarpConfigDto(
    string AcmeDirectoryUrl,
    string? AcmeCaBundlePath,
    string? AcmeEabKeyId,
    bool HasAcmeEabHmacKey,
    bool RedirectHttpToHttps,
    bool HttpsListenerBound);

/// <summary>Cloudflare Tunnel connection values for the config surface (token reduced to a flag).</summary>
public sealed record ProxyCloudflareConfigDto(
    string? AccountId,
    string? ZoneId,
    bool HasApiToken,
    string TunnelName,
    string? TeamDomain,
    bool Managed,
    string CloudflaredImage,
    string? CloudflaredContainerName,
    string AccessAllowedEmails,
    string AccessAllowedEmailDomains,
    string AccessGroupIds,
    string AccessReusablePolicyIds);
