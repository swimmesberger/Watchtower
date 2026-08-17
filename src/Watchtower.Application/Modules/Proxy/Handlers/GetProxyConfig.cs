using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Returns the reverse-proxy configuration for the Settings page. Everything here is
/// runtime-switchable: <see cref="CaddyManager"/> watches the options and starts, stops or
/// reconfigures the managed Caddy container when they change — no restart. Env-pinned paths ride
/// along so the UI disables those fields (env wins over the settings store).
/// </summary>
[Handler("proxy.getConfig")]
public sealed class GetProxyConfig(IOptionsMonitor<WatchtowerOptions> options, EnvironmentSettingPins pins)
    : IHandler<GetProxyConfig.Query, Result<GetProxyConfig.Response>> {
    public sealed record Query;
    public sealed record Response(
        bool Enabled,
        string? AdminEmail,
        string CaddyImage,
        string[] PinnedPaths);

    /// <summary>Every path the proxy card manages — shared with <see cref="UpdateProxyConfig"/>.</summary>
    internal static readonly string[] ProxyPaths = [
        WatchtowerSettingPaths.ProxyEnabled,
        WatchtowerSettingPaths.ProxyAdminEmail,
        WatchtowerSettingPaths.ProxyCaddyImage,
    ];

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var proxy = options.CurrentValue.Proxy;
        var response = new Response(
            Enabled: proxy.Enabled,
            AdminEmail: proxy.AdminEmail,
            CaddyImage: proxy.CaddyImage,
            PinnedPaths: pins.Pinned(ProxyPaths));
        return ValueTask.FromResult<Result<Response>>(response);
    }
}
