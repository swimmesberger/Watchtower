using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Elarion.Abstractions.Identity;
using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.InternalCa;
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
    YarpListenerState listener,
    WatchtowerDbContext db)
    : IHandler<UpdateProxyConfig.Command, Result<UpdateProxyConfig.Response>> {
    public sealed record Command(
        bool Enabled,
        string Provider,
        string? AdminEmail,
        string CaddyImage,
        int? YarpHttpPort = null,
        int? YarpHttpsPort = null,
        string? YarpAcmeDirectoryUrl = null,
        string? YarpAcmeCaBundlePath = null,
        string? YarpAcmeEabKeyId = null,
        string? YarpAcmeEabHmacKey = null,
        bool? YarpRedirectHttpToHttps = null,
        string? PortRoutesLanNames = null,
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
        var portRoutes = proxy.PortRoutes;

        // Effective in-process-proxy values after this update: supplied value, else what is configured.
        var acmeDirectoryUrl = Coalesce(command.YarpAcmeDirectoryUrl, yarp.AcmeDirectoryUrl) ?? "";
        var acmeCaBundlePath = Coalesce(command.YarpAcmeCaBundlePath, yarp.AcmeCaBundlePath);
        var acmeEabKeyId = Coalesce(command.YarpAcmeEabKeyId, yarp.AcmeEabKeyId);
        var acmeEabHmacKey = Coalesce(command.YarpAcmeEabHmacKey, yarp.AcmeEabHmacKey);
        var redirectHttpToHttps = command.YarpRedirectHttpToHttps ?? yarp.RedirectHttpToHttps;
        var lanNames = Coalesce(command.PortRoutesLanNames, portRoutes.LanNames) ?? "";
        var httpPort = command.YarpHttpPort ?? yarp.HttpPort;
        var httpsPort = command.YarpHttpsPort ?? yarp.HttpsPort;

        // A yarp value is checked when this request supplies it, and — supplied or not — whenever this
        // request switches the in-process provider ON, because that is the moment the stored values
        // start being acted on. Validating the coalesced values unconditionally would be worse than
        // useless: a CA bundle that vanished across a remount would then block "disable the proxy" and
        // "switch back to caddy", which are precisely the two things an operator does when the
        // certificate plane is broken.
        var enablingYarp = command.Enabled && provider == ProxyProviderNames.Yarp;

        // The ingress ports are checked whenever this request supplies one, and — supplied or not — when it
        // switches the in-process provider on, because that is the moment Kestrel is asked to bind them.
        // Unlike the ACME values these are cheap to check and cannot rot underneath us, but the rule is kept
        // the same so "disable the proxy" never fails on a value it is about to stop acting on.
        if (command.YarpHttpPort is not null || command.YarpHttpsPort is not null || enablingYarp) {
            if (ValidateIngressPorts(httpPort, httpsPort, listener.ManagementPort) is { } portError)
                return AppError.Validation(portError);
            // The other direction of the check proxy.createRoute makes: there it is a new listen port
            // against the stored ingress ports, here a new ingress port against the stored listen ports.
            // Whichever of the two is written second is refused, so neither order can reach the collision
            // — where it would surface as two Kestrel endpoints asked for one socket.
            if (await PortRouteCollisionAsync(httpPort, httpsPort, ct) is { } clash)
                return AppError.Validation(clash);
        }

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

        // Same rule as the ports, one gate wider: checked when supplied, and when this save switches the
        // proxy on under *any* provider, because that is the moment the internal CA starts issuing for
        // these names — port routes are served alongside Caddy and Cloudflare too (ADR-0033 addendum).
        // Empty is valid and means the internal CA is unused: a deployment with no LAN addresses to serve.
        if ((command.PortRoutesLanNames is not null || command.Enabled)
            && !InternalCaNames.TryParseLanNames(lanNames, out _, out _, out var lanReason))
            return AppError.Validation(
                $"{lanReason} List the host names and IP addresses this deployment is reached on, "
                + "separated by commas or newlines.");

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
        Check(WatchtowerSettingPaths.ProxyYarpHttpPort, httpPort != yarp.HttpPort);
        Check(WatchtowerSettingPaths.ProxyYarpHttpsPort, httpsPort != yarp.HttpsPort);
        Check(WatchtowerSettingPaths.ProxyYarpAcmeDirectoryUrl, Changed(command.YarpAcmeDirectoryUrl, yarp.AcmeDirectoryUrl));
        Check(WatchtowerSettingPaths.ProxyYarpAcmeCaBundlePath, Changed(command.YarpAcmeCaBundlePath, yarp.AcmeCaBundlePath));
        Check(WatchtowerSettingPaths.ProxyYarpAcmeEabKeyId, Changed(command.YarpAcmeEabKeyId, yarp.AcmeEabKeyId));
        Check(WatchtowerSettingPaths.ProxyYarpAcmeEabHmacKey, command.YarpAcmeEabHmacKey is not null);
        Check(WatchtowerSettingPaths.ProxyYarpRedirectHttpToHttps, redirectHttpToHttps != yarp.RedirectHttpToHttps);
        Check(WatchtowerSettingPaths.ProxyPortRoutesLanNames, Changed(command.PortRoutesLanNames, portRoutes.LanNames));
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

        // Write order matters, because every setting write raises a configuration reload of its own and
        // the ingress listeners follow that configuration. A save that changes several of these settings
        // is therefore seen by Kestrel as a sequence, and the sequence must never pass through a state the
        // operator did not ask for:
        //   * turning ingress ON — ports first, so the listeners only ever appear on the ports this save
        //     states. The other order would bind the previous (or default) port on the way and move it a
        //     moment later, publishing a port nobody asked for and failing loudly if something holds it.
        //   * turning ingress OFF — Enabled/Provider first, so the listeners are gone before the new port
        //     values land. The other order would briefly bind the new ports on a proxy that is being
        //     switched off.
        // "Ingress is on after this save" is the condition, not "Enabled": switching to Caddy takes the
        // listeners down just as disabling does.
        var ingressAfterSave = command.Enabled && provider == ProxyProviderNames.Yarp;

        async Task WritePortsAsync() {
            if (command.YarpHttpPort is not null)
                await SetUnlessPinnedAsync(
                    WatchtowerSettingPaths.ProxyYarpHttpPort,
                    httpPort.ToString(CultureInfo.InvariantCulture), ct);
            if (command.YarpHttpsPort is not null)
                await SetUnlessPinnedAsync(
                    WatchtowerSettingPaths.ProxyYarpHttpsPort,
                    httpsPort.ToString(CultureInfo.InvariantCulture), ct);
        }

        async Task WriteProviderAsync() {
            await SetUnlessPinnedAsync(
                WatchtowerSettingPaths.ProxyEnabled, command.Enabled ? "true" : "false", ct);
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyProvider, provider, ct);
        }

        if (ingressAfterSave) {
            await WritePortsAsync();
            await WriteProviderAsync();
        } else {
            await WriteProviderAsync();
            await WritePortsAsync();
        }

        await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyAdminEmail, email, ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.ProxyCaddyImage, image, ct);
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
        if (command.PortRoutesLanNames is not null)
            await SetUnlessPinnedAsync(
                WatchtowerSettingPaths.ProxyPortRoutesLanNames, command.PortRoutesLanNames.Trim(), ct);
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
                // The ACME host, not the whole URL: it is the part that says which CA will be asked — and
                // the ingress ports, because changing one rebinds a listener facing the internet.
                ProxyProviderNames.Yarp =>
                    $" · acme {AcmeHost(acmeDirectoryUrl)}"
                    + $" · ingress http {PortLabel(httpPort)}, https {PortLabel(httpsPort)}",
                _ => $" · image {image}",
            })
            // Outside the provider switch since the ADR-0033 addendum: the LAN names decide what the
            // internal CA issues for under every provider, and a name added or removed here changes which
            // devices can reach this deployment's port routes over TLS.
            + $" · lan names {(lanNames.Length > 0 ? lanNames : "none")}"
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
                HttpPort = httpPort,
                HttpsPort = httpsPort,
                AcmeDirectoryUrl = acmeDirectoryUrl,
                AcmeCaBundlePath = string.IsNullOrWhiteSpace(acmeCaBundlePath) ? null : acmeCaBundlePath,
                AcmeEabKeyId = string.IsNullOrWhiteSpace(acmeEabKeyId) ? null : acmeEabKeyId,
                AcmeEabHmacKey = string.IsNullOrWhiteSpace(acmeEabHmacKey) ? null : acmeEabHmacKey,
                RedirectHttpToHttps = redirectHttpToHttps,
            },
            PortRoutes = portRoutes with { LanNames = lanNames },
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
    /// Checks the two ingress ports, returning the operator-facing message or null. <c>0</c> is a real
    /// answer — "do not bind that listener" — so the range check has to admit it; everything else is about
    /// two listeners not being able to come up at all. The management port is only known once the host has
    /// derived it, and a collision with it would take the UI down with the listener that stole it.
    /// </summary>
    private static string? ValidateIngressPorts(int httpPort, int httpsPort, int? managementPort) {
        if (httpPort is < 0 or > 65535 || httpsPort is < 0 or > 65535)
            return "An ingress port must be between 1 and 65535, or 0 to turn that listener off.";
        if (httpPort != 0 && httpPort == httpsPort)
            return "The HTTP and HTTPS ingress ports must differ.";
        if (managementPort is { } management && (httpPort == management || httpsPort == management))
            return $"An ingress port must not be the management port ({management}) — "
                + "that is the listener Watchtower's own UI and API are served on.";
        return null;
    }

    /// <summary>
    /// Checks the two ingress ports against the ports the existing port routes already listen on
    /// (ADR-0033), returning the operator-facing message naming the route in the way, or null.
    /// </summary>
    /// <remarks>
    /// The projection drops a port-route listener whose port collides with an ingress one, so without
    /// this the route would keep its row, quietly stop being served, and read as healthy on the Routes
    /// page. Off is never a collision: a listener that is not bound cannot take anything.
    /// </remarks>
    private async ValueTask<string?> PortRouteCollisionAsync(int httpPort, int httpsPort, CancellationToken ct) {
        var wanted = new List<int>();
        if (httpPort != 0) wanted.Add(httpPort);
        if (httpsPort != 0) wanted.Add(httpsPort);
        if (wanted.Count == 0) return null;

        var clash = await db.Routes.AsNoTracking()
            .Where(r => r.ListenPort != null && wanted.Contains(r.ListenPort.Value))
            .Select(r => new { r.Id, r.ListenPort, r.ServiceName })
            .FirstOrDefaultAsync(ct);
        if (clash is null) return null;

        var which = clash.ListenPort == httpPort ? "HTTP" : "HTTPS";
        return $"The {which} ingress port {clash.ListenPort} is route {clash.Id}'s listen port "
            + $"({clash.ServiceName}). Move that port route first, or choose another ingress port.";
    }

    /// <summary>A port for the audit line, with <c>0</c> spelled out as what it means.</summary>
    private static string PortLabel(int port) =>
        port == 0 ? "off" : port.ToString(CultureInfo.InvariantCulture);

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
