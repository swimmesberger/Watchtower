namespace Watchtower.Application.Config;

/// <summary>
/// The configuration paths of the runtime-editable settings, shared by the settings handlers (which
/// persist them through the Elarion settings store) and the environment-pin reporting
/// (<see cref="Services.EnvironmentSettingPins"/>). Each path maps onto its conventional environment
/// variable by replacing <c>:</c> with <c>__</c> and upper-casing (e.g. <see cref="AuthEnabled"/> ⇔
/// <c>WATCHTOWER__AUTH__ENABLED</c>). Environment variables win over stored settings — see the
/// configuration layering in <c>Program.cs</c> — so a path set via its env var is pinned: visible,
/// but not editable at runtime.
/// </summary>
public static class WatchtowerSettingPaths {
    // ── Automation (system.updateAutomation) ─────────────────────────────────
    public const string AutoCheckEnabled = "Watchtower:AutoCheckEnabled";
    public const string AutoCheckIntervalMinutes = "Watchtower:AutoCheckIntervalMinutes";
    public const string StackCheckEnabled = "Watchtower:StackCheckEnabled";
    public const string StackCheckIntervalMinutes = "Watchtower:StackCheckIntervalMinutes";
    public const string ImagePruneEnabled = "Watchtower:ImagePruneEnabled";
    public const string ImagePruneIntervalMinutes = "Watchtower:ImagePruneIntervalMinutes";

    // ── Metrics (metrics.updateConfig) ───────────────────────────────────────
    public const string MetricsBackend = "Watchtower:Metrics:Backend";
    public const string MetricsRetentionDays = "Watchtower:Metrics:RetentionDays";
    public const string MetricsInfluxUrl = "Watchtower:Metrics:Influx:Url";
    public const string MetricsInfluxOrg = "Watchtower:Metrics:Influx:Org";
    public const string MetricsInfluxBucket = "Watchtower:Metrics:Influx:Bucket";
    public const string MetricsInfluxToken = "Watchtower:Metrics:Influx:Token";
    public const string MetricsInfluxComposeProjectTag = "Watchtower:Metrics:Influx:ComposeProjectTag";
    public const string MetricsInfluxDiskMountpoint = "Watchtower:Metrics:Influx:DiskMountpoint";

    // ── Auth (system.updateAuthConfig) ───────────────────────────────────────
    public const string AuthEnabled = "Watchtower:Auth:Enabled";
    public const string AuthHost = "Watchtower:Auth:Host";
    public const string AuthSessionLifetimeHours = "Watchtower:Auth:SessionLifetimeHours";
    public const string AuthAbsoluteSessionLifetimeDays = "Watchtower:Auth:AbsoluteSessionLifetimeDays";

    // ── Proxy (proxy.updateConfig) ───────────────────────────────────────────
    public const string ProxyEnabled = "Watchtower:Proxy:Enabled";
    public const string ProxyProvider = "Watchtower:Proxy:Provider";
    public const string ProxyAdminEmail = "Watchtower:Proxy:AdminEmail";
    public const string ProxyCaddyImage = "Watchtower:Proxy:CaddyImage";
    public const string ProxyCloudflareAccountId = "Watchtower:Proxy:Cloudflare:AccountId";
    public const string ProxyCloudflareZoneId = "Watchtower:Proxy:Cloudflare:ZoneId";
    public const string ProxyCloudflareApiToken = "Watchtower:Proxy:Cloudflare:ApiToken";
    public const string ProxyCloudflareTunnelName = "Watchtower:Proxy:Cloudflare:TunnelName";
    public const string ProxyCloudflareTeamDomain = "Watchtower:Proxy:Cloudflare:TeamDomain";
    public const string ProxyCloudflareManaged = "Watchtower:Proxy:Cloudflare:Managed";
    public const string ProxyCloudflareCloudflaredImage = "Watchtower:Proxy:Cloudflare:CloudflaredImage";
    public const string ProxyCloudflareCloudflaredContainerName = "Watchtower:Proxy:Cloudflare:CloudflaredContainerName";
    public const string ProxyCloudflareAccessAllowedEmails = "Watchtower:Proxy:Cloudflare:AccessAllowedEmails";
    public const string ProxyCloudflareAccessAllowedEmailDomains = "Watchtower:Proxy:Cloudflare:AccessAllowedEmailDomains";
    public const string ProxyCloudflareAccessGroupIds = "Watchtower:Proxy:Cloudflare:AccessGroupIds";
    public const string ProxyCloudflareAccessReusablePolicyIds = "Watchtower:Proxy:Cloudflare:AccessReusablePolicyIds";
}
