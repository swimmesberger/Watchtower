using Elarion.Settings;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Persists the reverse-proxy settings as Global-scope settings under <c>Watchtower:Proxy:*</c>. No
/// explicit trigger is needed: both providers subscribe to the options monitor and react once the
/// settings provider re-binds — enabling reconciles the selected provider's topology, disabling (or
/// switching provider) tears the old data plane down, other changes refresh the configuration
/// (ADR-0015). A null <see cref="Command.CloudflareApiToken"/> keeps the stored token, so the UI never
/// has to echo the secret. Env-pinned paths (env wins over the store) are rejected when the request
/// tries to change them, and never written.
/// </summary>
[Handler("proxy.updateConfig")]
public sealed class UpdateProxyConfig(
    ISettingsManager settings,
    IOptionsMonitor<WatchtowerOptions> options,
    CloudflareApiClient cloudflare,
    EnvironmentSettingPins pins)
    : IHandler<UpdateProxyConfig.Command, Result<UpdateProxyConfig.Response>> {
    public sealed record Command(
        bool Enabled,
        string Provider,
        string? AdminEmail,
        string CaddyImage,
        string? CloudflareAccountId = null,
        string? CloudflareZoneId = null,
        string? CloudflareApiToken = null,
        string? CloudflareTunnelName = null,
        bool? CloudflareManaged = null,
        string? CloudflaredImage = null,
        string? CloudflaredContainerName = null,
        string? CloudflareAccessAllowedEmails = null,
        string? CloudflareAccessAllowedEmailDomains = null);

    public sealed record Response(ProxyConfigDto Config);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var provider = command.Provider.Trim().ToLowerInvariant();
        if (provider is not ("caddy" or "cloudflare"))
            return AppError.Validation("Provider must be one of: caddy, cloudflare.");

        var email = command.AdminEmail?.Trim() ?? "";
        if (email.Length > 0 && (!email.Contains('@') || email.Contains(' ')))
            return AppError.Validation("AdminEmail must be an email address (or empty).");
        var image = command.CaddyImage?.Trim() ?? "";
        if (image.Length == 0)
            return AppError.Validation("CaddyImage is required (default: caddy:2).");
        if (image.Contains(' '))
            return AppError.Validation("CaddyImage must be a single image reference.");

        var proxy = options.CurrentValue.Proxy;
        var cf = proxy.Cloudflare;

        // Effective cloudflare values after this update: supplied value, else what is already configured.
        var accountId = Coalesce(command.CloudflareAccountId, cf.AccountId);
        var zoneId = Coalesce(command.CloudflareZoneId, cf.ZoneId);
        var apiToken = Coalesce(command.CloudflareApiToken, cf.ApiToken);
        var tunnelName = Coalesce(command.CloudflareTunnelName, cf.TunnelName) ?? "";
        var managed = command.CloudflareManaged ?? cf.Managed;
        var cloudflaredImage = Coalesce(command.CloudflaredImage, cf.CloudflaredImage) ?? "";
        var containerName = Coalesce(command.CloudflaredContainerName, cf.CloudflaredContainerName);

        if (command.Enabled && provider == "cloudflare") {
            if (string.IsNullOrWhiteSpace(accountId)) return AppError.Validation("The Cloudflare account id is required for the cloudflare provider.");
            if (string.IsNullOrWhiteSpace(zoneId)) return AppError.Validation("The Cloudflare zone id is required for the cloudflare provider.");
            if (string.IsNullOrWhiteSpace(apiToken)) return AppError.Validation("A Cloudflare API token is required for the cloudflare provider.");
            if (string.IsNullOrWhiteSpace(tunnelName)) return AppError.Validation("The tunnel name is required for the cloudflare provider.");
            if (string.IsNullOrWhiteSpace(cloudflaredImage) && managed) return AppError.Validation("The cloudflared image is required in managed mode.");
            // Probe the credentials before persisting so a typo'd token fails here with Cloudflare's own
            // words, not later as a background reconcile warning nobody is watching for.
            if (await cloudflare.ValidateAccessAsync(accountId!, apiToken!, ct) is { } reason)
                return AppError.Validation($"Cloudflare rejected the credentials: {reason}");
        }

        // Reject changes to env-pinned paths (env wins — a stored row would silently not take effect).
        var violations = new List<string>();
        void Check(string path, bool changed) {
            if (changed && pins.IsPinned(path)) violations.Add(path);
        }
        Check(WatchtowerSettingPaths.ProxyEnabled, command.Enabled != proxy.Enabled);
        Check(WatchtowerSettingPaths.ProxyProvider,
            provider != (proxy.ResolveProvider() == ProxyProviderKind.Cloudflare ? "cloudflare" : "caddy"));
        Check(WatchtowerSettingPaths.ProxyAdminEmail, !string.Equals(email, proxy.AdminEmail?.Trim() ?? "", StringComparison.Ordinal));
        Check(WatchtowerSettingPaths.ProxyCaddyImage, !string.Equals(image, proxy.CaddyImage.Trim(), StringComparison.Ordinal));
        Check(WatchtowerSettingPaths.ProxyCloudflareAccountId, Changed(command.CloudflareAccountId, cf.AccountId));
        Check(WatchtowerSettingPaths.ProxyCloudflareZoneId, Changed(command.CloudflareZoneId, cf.ZoneId));
        Check(WatchtowerSettingPaths.ProxyCloudflareApiToken, command.CloudflareApiToken is not null);
        Check(WatchtowerSettingPaths.ProxyCloudflareTunnelName, Changed(command.CloudflareTunnelName, cf.TunnelName));
        Check(WatchtowerSettingPaths.ProxyCloudflareManaged, managed != cf.Managed);
        Check(WatchtowerSettingPaths.ProxyCloudflareCloudflaredImage, Changed(command.CloudflaredImage, cf.CloudflaredImage));
        Check(WatchtowerSettingPaths.ProxyCloudflareCloudflaredContainerName, Changed(command.CloudflaredContainerName, cf.CloudflaredContainerName));
        Check(WatchtowerSettingPaths.ProxyCloudflareAccessAllowedEmails, Changed(command.CloudflareAccessAllowedEmails, cf.AccessAllowedEmails));
        Check(WatchtowerSettingPaths.ProxyCloudflareAccessAllowedEmailDomains, Changed(command.CloudflareAccessAllowedEmailDomains, cf.AccessAllowedEmailDomains));
        if (violations.Count > 0)
            return EnvironmentSettingPins.PinnedError(violations);

        await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyEnabled, command.Enabled ? "true" : "false", ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyProvider, provider, ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyAdminEmail, email, ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCaddyImage, image, ct);
        if (command.CloudflareAccountId is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCloudflareAccountId, command.CloudflareAccountId.Trim(), ct);
        if (command.CloudflareZoneId is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCloudflareZoneId, command.CloudflareZoneId.Trim(), ct);
        if (command.CloudflareApiToken is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCloudflareApiToken, command.CloudflareApiToken.Trim(), ct);
        if (command.CloudflareTunnelName is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCloudflareTunnelName, command.CloudflareTunnelName.Trim(), ct);
        if (command.CloudflareManaged is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCloudflareManaged, managed ? "true" : "false", ct);
        if (command.CloudflaredImage is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCloudflareCloudflaredImage, command.CloudflaredImage.Trim(), ct);
        if (command.CloudflaredContainerName is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCloudflareCloudflaredContainerName, command.CloudflaredContainerName.Trim(), ct);
        if (command.CloudflareAccessAllowedEmails is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCloudflareAccessAllowedEmails, command.CloudflareAccessAllowedEmails.Trim(), ct);
        if (command.CloudflareAccessAllowedEmailDomains is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCloudflareAccessAllowedEmailDomains, command.CloudflareAccessAllowedEmailDomains.Trim(), ct);

        // Echo the written values (the config provider reloads asynchronously — same reasoning as
        // system.updateAutomation): immediately consistent for the caller.
        var echoed = proxy with {
            Enabled = command.Enabled,
            Provider = provider,
            AdminEmail = email.Length > 0 ? email : null,
            CaddyImage = image,
            Cloudflare = cf with {
                AccountId = accountId,
                ZoneId = zoneId,
                ApiToken = apiToken,
                TunnelName = tunnelName,
                Managed = managed,
                CloudflaredImage = cloudflaredImage,
                CloudflaredContainerName = string.IsNullOrWhiteSpace(containerName) ? null : containerName,
                AccessAllowedEmails = Coalesce(command.CloudflareAccessAllowedEmails, cf.AccessAllowedEmails) ?? "",
                AccessAllowedEmailDomains = Coalesce(command.CloudflareAccessAllowedEmailDomains, cf.AccessAllowedEmailDomains) ?? "",
            },
        };
        return new Response(ProxyConfigDto.From(echoed, pins));
    }

    private Task SetUnlessPinnedAsync(string path, string value, CancellationToken ct) =>
        pins.IsPinned(path)
            ? Task.CompletedTask
            : settings.SetStringAsync(path, value, SettingsScope.Global, expectedVersion: null, ct).AsTask();

    private static string? Coalesce(string? supplied, string? existing) =>
        supplied is null ? existing : supplied.Trim();

    /// <summary>An omitted field never changes anything; empty and null are the same stored "unset".</summary>
    private static bool Changed(string? supplied, string? existing) =>
        supplied is not null
        && !string.Equals(supplied.Trim(), existing ?? "", StringComparison.Ordinal);
}
