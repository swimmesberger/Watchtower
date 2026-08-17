using Elarion.Settings;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Persists the reverse-proxy settings as Global-scope settings under <c>Watchtower:Proxy:*</c>. No
/// explicit trigger is needed: <see cref="CaddyManager"/> subscribes to the options monitor and reacts
/// once the settings provider re-binds — enabling reconciles the full topology (networks, container,
/// routes), disabling stops and removes the managed Caddy container (networks and the certificate
/// volume are kept), an email change re-renders the config. Env-pinned paths (env wins over the store)
/// are rejected when the request tries to change them, and never written.
/// </summary>
[Handler("proxy.updateConfig")]
public sealed class UpdateProxyConfig(
    ISettingsManager settings, IOptionsMonitor<WatchtowerOptions> options, EnvironmentSettingPins pins)
    : IHandler<UpdateProxyConfig.Command, Result<UpdateProxyConfig.Response>> {
    public sealed record Command(bool Enabled, string? AdminEmail, string CaddyImage);

    public sealed record Response(
        bool Enabled,
        string? AdminEmail,
        string CaddyImage,
        string[] PinnedPaths);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var email = command.AdminEmail?.Trim() ?? "";
        if (email.Length > 0 && (!email.Contains('@') || email.Contains(' ')))
            return AppError.Validation("AdminEmail must be an email address (or empty).");
        var image = command.CaddyImage?.Trim() ?? "";
        if (image.Length == 0)
            return AppError.Validation("CaddyImage is required (default: caddy:2).");
        if (image.Contains(' '))
            return AppError.Validation("CaddyImage must be a single image reference.");

        var proxy = options.CurrentValue.Proxy;
        var violations = new List<string>();
        void Check(string path, bool changed) {
            if (changed && pins.IsPinned(path)) violations.Add(path);
        }
        Check(WatchtowerSettingPaths.ProxyEnabled, command.Enabled != proxy.Enabled);
        Check(WatchtowerSettingPaths.ProxyAdminEmail, !string.Equals(email, proxy.AdminEmail?.Trim() ?? "", StringComparison.Ordinal));
        Check(WatchtowerSettingPaths.ProxyCaddyImage, !string.Equals(image, proxy.CaddyImage.Trim(), StringComparison.Ordinal));
        if (violations.Count > 0)
            return EnvironmentSettingPins.PinnedError(violations);

        await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyEnabled, command.Enabled ? "true" : "false", ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyAdminEmail, email, ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCaddyImage, image, ct);

        // Echo the written values (the config provider reloads asynchronously — same reasoning as
        // system.updateAutomation): immediately consistent for the caller.
        return new Response(
            Enabled: command.Enabled,
            AdminEmail: email.Length > 0 ? email : null,
            CaddyImage: image,
            PinnedPaths: pins.Pinned(GetProxyConfig.ProxyPaths));
    }

    private Task SetUnlessPinnedAsync(string path, string value, CancellationToken ct) =>
        pins.IsPinned(path)
            ? Task.CompletedTask
            : settings.SetStringAsync(path, value, SettingsScope.Global, expectedVersion: null, ct).AsTask();
}
