using Elarion.Abstractions.Identity;
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
    EnvironmentSettingPins pins,
    AuditLog audit,
    ICurrentUser currentUser)
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
        string? CloudflareTeamDomain = null,
        bool? CloudflareManaged = null,
        string? CloudflaredImage = null,
        string? CloudflaredContainerName = null,
        string? CloudflareAccessAllowedEmails = null,
        string? CloudflareAccessAllowedEmailDomains = null,
        string? CloudflareAccessGroupIds = null,
        string? CloudflareAccessReusablePolicyIds = null);

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
            // words, not later as a background reconcile warning nobody is watching for. Both scopes:
            // the account (tunnels) AND the zone (DNS records) — a token that can manage tunnels but not
            // this zone's DNS leaves every route stuck with a failing CNAME upsert.
            if (await cloudflare.ValidateAccessAsync(accountId!, apiToken!, ct) is { } reason)
                return AppError.Validation($"Cloudflare rejected the credentials: {reason}");
            if (await cloudflare.ValidateZoneAccessAsync(zoneId!, apiToken!, ct) is { } zoneReason)
                return AppError.Validation(
                    $"Cloudflare rejected the token for zone {zoneId}: {zoneReason}. The token needs "
                    + "Zone → DNS → Edit on the zone your route domains live under, and the Zone ID must be that zone's.");
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
        Check(WatchtowerSettingPaths.ProxyCloudflareTeamDomain, Changed(command.CloudflareTeamDomain, cf.TeamDomain));
        Check(WatchtowerSettingPaths.ProxyCloudflareManaged, managed != cf.Managed);
        Check(WatchtowerSettingPaths.ProxyCloudflareCloudflaredImage, Changed(command.CloudflaredImage, cf.CloudflaredImage));
        Check(WatchtowerSettingPaths.ProxyCloudflareCloudflaredContainerName, Changed(command.CloudflaredContainerName, cf.CloudflaredContainerName));
        Check(WatchtowerSettingPaths.ProxyCloudflareAccessAllowedEmails, Changed(command.CloudflareAccessAllowedEmails, cf.AccessAllowedEmails));
        Check(WatchtowerSettingPaths.ProxyCloudflareAccessAllowedEmailDomains, Changed(command.CloudflareAccessAllowedEmailDomains, cf.AccessAllowedEmailDomains));
        Check(WatchtowerSettingPaths.ProxyCloudflareAccessGroupIds, Changed(command.CloudflareAccessGroupIds, cf.AccessGroupIds));
        Check(WatchtowerSettingPaths.ProxyCloudflareAccessReusablePolicyIds, Changed(command.CloudflareAccessReusablePolicyIds, cf.AccessReusablePolicyIds));
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
        if (command.CloudflareTeamDomain is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCloudflareTeamDomain, command.CloudflareTeamDomain.Trim(), ct);
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
        if (command.CloudflareAccessGroupIds is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCloudflareAccessGroupIds, command.CloudflareAccessGroupIds.Trim(), ct);
        if (command.CloudflareAccessReusablePolicyIds is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCloudflareAccessReusablePolicyIds, command.CloudflareAccessReusablePolicyIds.Trim(), ct);

        // Recorded post-write with the new effective values — secrets appear only as "updated".
        // Category "proxy" so the row also lands in the Routes page's proxy-scoped audit slice.
        await audit.RecordAsync("proxy", "config.update", "proxy settings",
            (command.Enabled ? "enabled" : "disabled")
            + $" · provider {provider}"
            + (provider == "cloudflare"
                ? $" · tunnel {tunnelName}" + (managed ? " · managed cloudflared" : "")
                : $" · image {image}")
            + (command.CloudflareApiToken is not null ? " · secrets updated: Cloudflare API token" : ""),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

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
                TeamDomain = Coalesce(command.CloudflareTeamDomain, cf.TeamDomain),
                Managed = managed,
                CloudflaredImage = cloudflaredImage,
                CloudflaredContainerName = string.IsNullOrWhiteSpace(containerName) ? null : containerName,
                AccessAllowedEmails = Coalesce(command.CloudflareAccessAllowedEmails, cf.AccessAllowedEmails) ?? "",
                AccessAllowedEmailDomains = Coalesce(command.CloudflareAccessAllowedEmailDomains, cf.AccessAllowedEmailDomains) ?? "",
                AccessGroupIds = Coalesce(command.CloudflareAccessGroupIds, cf.AccessGroupIds) ?? "",
                AccessReusablePolicyIds = Coalesce(command.CloudflareAccessReusablePolicyIds, cf.AccessReusablePolicyIds) ?? "",
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
