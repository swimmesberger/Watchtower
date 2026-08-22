using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Elarion.Abstractions.Identity;
using Elarion.Settings;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Persists the reverse-proxy settings as Global-scope settings under <c>Watchtower:Proxy:*</c>. No
/// explicit trigger is needed: both providers subscribe to the options monitor and react once the
/// settings provider re-binds — enabling reconciles the selected provider's topology, disabling (or
/// switching provider) tears the old data plane down, other changes refresh the configuration
/// (ADR-0015). A null secret field (<see cref="Command.CloudflareApiToken"/>,
/// <see cref="Command.YarpAcmeEabHmacKey"/>) keeps the stored value, so the UI never has to echo a
/// secret. Env-pinned paths (env wins over the store) are rejected when the request tries to change
/// them, and never written.
/// </summary>
[Handler("proxy.updateConfig")]
public sealed class UpdateProxyConfig(
    ISettingsManager settings,
    IOptionsMonitor<WatchtowerOptions> options,
    CloudflareApiClient cloudflare,
    EnvironmentSettingPins pins,
    AuditLog audit,
    ICurrentUser currentUser,
    YarpListenerState listener)
    : IHandler<UpdateProxyConfig.Command, Result<UpdateProxyConfig.Response>> {
    public sealed record Command(
        bool Enabled,
        string Provider,
        string? AdminEmail,
        string CaddyImage,
        string? YarpAcmeDirectoryUrl = null,
        string? YarpAcmeCaBundlePath = null,
        string? YarpAcmeEabKeyId = null,
        string? YarpAcmeEabHmacKey = null,
        bool? YarpRedirectHttpToHttps = null,
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
        if (!ProxyProviderNames.All.Contains(provider, StringComparer.Ordinal))
            return AppError.Validation(
                $"Provider must be one of: {string.Join(", ", ProxyProviderNames.All)}.");

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
        var yarp = proxy.Yarp;

        // Effective in-process-proxy values after this update: supplied value, else what is configured.
        var acmeDirectoryUrl = Coalesce(command.YarpAcmeDirectoryUrl, yarp.AcmeDirectoryUrl) ?? "";
        var acmeCaBundlePath = Coalesce(command.YarpAcmeCaBundlePath, yarp.AcmeCaBundlePath);
        var acmeEabKeyId = Coalesce(command.YarpAcmeEabKeyId, yarp.AcmeEabKeyId);
        var acmeEabHmacKey = Coalesce(command.YarpAcmeEabHmacKey, yarp.AcmeEabHmacKey);
        var redirectHttpToHttps = command.YarpRedirectHttpToHttps ?? yarp.RedirectHttpToHttps;

        // A yarp value is checked when this request supplies it, and — supplied or not — whenever this
        // request switches the in-process provider ON, because that is the moment the stored values
        // start being acted on. Validating the coalesced values unconditionally would be worse than
        // useless: a CA bundle that vanished across a remount would then block "disable the proxy" and
        // "switch back to caddy", which are precisely the two things an operator does when the
        // certificate plane is broken.
        var enablingYarp = command.Enabled && provider == ProxyProviderNames.Yarp;
        if ((command.YarpAcmeDirectoryUrl is not null || enablingYarp)
            && !IsAcceptableAcmeDirectoryUrl(acmeDirectoryUrl))
            // Empty is rejected on purpose: unlike the optional fields below, where empty means "unset",
            // the proxy has no CA to talk to without a directory URL.
            return AppError.Validation(
                "The ACME directory URL must be an absolute https URL (http is allowed only for a loopback address).");

        if ((command.YarpAcmeCaBundlePath is not null || enablingYarp)
            && ValidateAcmeCaBundle(acmeCaBundlePath) is { } bundleError)
            return AppError.Validation(bundleError);

        if ((command.YarpAcmeEabKeyId is not null || command.YarpAcmeEabHmacKey is not null || enablingYarp)
            && ValidateAcmeEab(acmeEabKeyId, acmeEabHmacKey) is { } eabError)
            return AppError.Validation(eabError);

        // Effective cloudflare values after this update: supplied value, else what is already configured.
        var accountId = Coalesce(command.CloudflareAccountId, cf.AccountId);
        var zoneId = Coalesce(command.CloudflareZoneId, cf.ZoneId);
        var apiToken = Coalesce(command.CloudflareApiToken, cf.ApiToken);
        var tunnelName = Coalesce(command.CloudflareTunnelName, cf.TunnelName) ?? "";
        var managed = command.CloudflareManaged ?? cf.Managed;
        var cloudflaredImage = Coalesce(command.CloudflaredImage, cf.CloudflaredImage) ?? "";
        var containerName = Coalesce(command.CloudflaredContainerName, cf.CloudflaredContainerName);

        if (command.Enabled && provider == ProxyProviderNames.Cloudflare) {
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
        Check(WatchtowerSettingPaths.ProxyProvider, provider != proxy.ProviderName());
        Check(WatchtowerSettingPaths.ProxyAdminEmail, !string.Equals(email, proxy.AdminEmail?.Trim() ?? "", StringComparison.Ordinal));
        Check(WatchtowerSettingPaths.ProxyCaddyImage, !string.Equals(image, proxy.CaddyImage.Trim(), StringComparison.Ordinal));
        Check(WatchtowerSettingPaths.ProxyYarpAcmeDirectoryUrl, Changed(command.YarpAcmeDirectoryUrl, yarp.AcmeDirectoryUrl));
        Check(WatchtowerSettingPaths.ProxyYarpAcmeCaBundlePath, Changed(command.YarpAcmeCaBundlePath, yarp.AcmeCaBundlePath));
        Check(WatchtowerSettingPaths.ProxyYarpAcmeEabKeyId, Changed(command.YarpAcmeEabKeyId, yarp.AcmeEabKeyId));
        Check(WatchtowerSettingPaths.ProxyYarpAcmeEabHmacKey, command.YarpAcmeEabHmacKey is not null);
        Check(WatchtowerSettingPaths.ProxyYarpRedirectHttpToHttps, redirectHttpToHttps != yarp.RedirectHttpToHttps);
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
        // Proxy:Yarp:CertPath is deliberately absent: it is read at bind time (the directory is created
        // and the certificate store opened over it at startup), so a runtime write would persist a value
        // nothing acts on until the next restart.
        if (command.YarpAcmeDirectoryUrl is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyYarpAcmeDirectoryUrl, command.YarpAcmeDirectoryUrl.Trim(), ct);
        if (command.YarpAcmeCaBundlePath is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyYarpAcmeCaBundlePath, command.YarpAcmeCaBundlePath.Trim(), ct);
        if (command.YarpAcmeEabKeyId is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyYarpAcmeEabKeyId, command.YarpAcmeEabKeyId.Trim(), ct);
        if (command.YarpAcmeEabHmacKey is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyYarpAcmeEabHmacKey, command.YarpAcmeEabHmacKey.Trim(), ct);
        if (command.YarpRedirectHttpToHttps is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyYarpRedirectHttpToHttps, redirectHttpToHttps ? "true" : "false", ct);
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

        // Named, never valued: the trail says which secrets this save replaced, not what with.
        var secretsUpdated = new List<string>();
        if (command.CloudflareApiToken is not null) secretsUpdated.Add("Cloudflare API token");
        if (command.YarpAcmeEabHmacKey is not null) secretsUpdated.Add("ACME EAB HMAC key");

        // Recorded post-write with the new effective values — secrets appear only as "updated".
        // Category "proxy" so the row also lands in the Routes page's proxy-scoped audit slice.
        await audit.RecordAsync("proxy", "config.update", "proxy settings",
            (command.Enabled ? "enabled" : "disabled")
            + $" · provider {provider}"
            + (provider switch {
                ProxyProviderNames.Cloudflare =>
                    $" · tunnel {tunnelName}" + (managed ? " · managed cloudflared" : ""),
                // The ACME host, not the whole URL: it is the part that says which CA will be asked.
                ProxyProviderNames.Yarp => $" · acme {AcmeHost(acmeDirectoryUrl)}",
                _ => $" · image {image}",
            })
            + (secretsUpdated.Count > 0 ? $" · secrets updated: {string.Join(", ", secretsUpdated)}" : ""),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        // Echo the written values (the config provider reloads asynchronously — same reasoning as
        // system.updateAutomation): immediately consistent for the caller.
        var echoed = proxy with {
            Enabled = command.Enabled,
            Provider = provider,
            AdminEmail = email.Length > 0 ? email : null,
            CaddyImage = image,
            Yarp = yarp with {
                AcmeDirectoryUrl = acmeDirectoryUrl,
                AcmeCaBundlePath = string.IsNullOrWhiteSpace(acmeCaBundlePath) ? null : acmeCaBundlePath,
                AcmeEabKeyId = string.IsNullOrWhiteSpace(acmeEabKeyId) ? null : acmeEabKeyId,
                AcmeEabHmacKey = string.IsNullOrWhiteSpace(acmeEabHmacKey) ? null : acmeEabHmacKey,
                RedirectHttpToHttps = redirectHttpToHttps,
            },
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
        return new Response(ProxyConfigDto.From(echoed, pins, listener.HttpsBound));
    }

    /// <summary>
    /// Checks that the extra ACME trust roots can actually be read, returning the operator-facing
    /// message or null. A bundle path that does not resolve is a certificate plane that will not
    /// start, so it is worth saying now rather than letting every issuance fail with a TLS error
    /// against the directory. Empty means "system trust only" and is always fine.
    /// </summary>
    private static string? ValidateAcmeCaBundle(string? caBundlePath) {
        if (string.IsNullOrWhiteSpace(caBundlePath)) return null;
        var path = caBundlePath.Trim();
        if (!Path.IsPathRooted(path))
            return "The ACME CA bundle path must be absolute.";
        if (!File.Exists(path))
            return $"The ACME CA bundle was not found at {path}.";
        try {
            var roots = new X509Certificate2Collection();
            roots.ImportFromPemFile(path);
            if (roots.Count == 0)
                return "The ACME CA bundle contains no certificates.";
        } catch (Exception ex) {
            // Verbatim: the parser's own words name the malformed part far better than we could.
            return $"The ACME CA bundle could not be read: {ex.Message}";
        }
        return null;
    }

    /// <summary>
    /// Checks the External Account Binding pair, returning the operator-facing message or null. EAB is
    /// a pair by definition; half of one binds to nothing and would fail at account registration.
    /// </summary>
    private static string? ValidateAcmeEab(string? eabKeyId, string? eabHmacKey) {
        var hasKeyId = !string.IsNullOrWhiteSpace(eabKeyId);
        var hasHmac = !string.IsNullOrWhiteSpace(eabHmacKey);
        if (hasKeyId != hasHmac)
            return "The ACME EAB key id and HMAC key must be set together (or both left empty).";
        if (hasHmac && !Base64Url.IsValid(eabHmacKey!.Trim()))
            return "The ACME EAB HMAC key must be base64url-encoded.";
        return null;
    }

    /// <summary>
    /// An absolute https directory URL — or http, but only against a loopback address, which is what
    /// makes a local pebble/step-ca test instance usable without opening a plaintext ACME path to the
    /// network.
    /// </summary>
    private static bool IsAcceptableAcmeDirectoryUrl(string url) {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        if (uri.Scheme != Uri.UriSchemeHttp) return false;
        // Uri.Host keeps the brackets around an IPv6 literal; IPAddress.TryParse does not want them.
        var host = uri.Host.Trim('[', ']');
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }

    /// <summary>The directory URL's host for the audit line, falling back to the raw value.</summary>
    private static string AcmeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

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
