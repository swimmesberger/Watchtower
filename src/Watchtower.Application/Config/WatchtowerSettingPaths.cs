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

    /// <summary>
    /// Internal marker: <see cref="Services.LoginHostConversion"/> has already turned this installation's
    /// configured <c>Auth:Host</c> into a Watchtower route (ADR-0023). Not a user setting — never offered
    /// in the UI and deliberately not env-pinnable. It exists so an operator who deletes the converted
    /// route does not find it recreated on the next restart.
    /// </summary>
    public const string AuthLoginHostsConverted = "Watchtower:Auth:LoginHostsConverted";

    /// <summary>
    /// Internal marker: <see cref="Services.FileStateImport"/> has already carried a pre-ADR-0024
    /// installation's key and certificate files into the database. Not a user setting — never offered in
    /// the UI and deliberately not env-pinnable. It is what makes the import one-shot: the files are
    /// never deleted, so without a sentinel every restart would re-read them and undo whatever has
    /// happened to the rows since.
    /// </summary>
    public const string AuthFileStateImported = "Watchtower:Auth:FileStateImported";

    // ── Proxy (proxy.updateConfig) ───────────────────────────────────────────
    public const string ProxyEnabled = "Watchtower:Proxy:Enabled";
    public const string ProxyProvider = "Watchtower:Proxy:Provider";

    /// <summary>
    /// Internal marker: <see cref="Services.ProxyProviderMigration"/> has already decided whether this
    /// installation predates ADR-0022's default flip. Not a user setting — never offered in the UI, never
    /// listed among the proxy card's paths, and deliberately not env-pinnable. It exists because the
    /// question the migration answers ("did this instance rely on the old implicit caddy default?") stops
    /// being answerable the moment the instance adds its first route under the new default.
    /// </summary>
    public const string ProxyProviderMigrated = "Watchtower:Proxy:ProviderMigrated";

    /// <summary>
    /// Internal marker: the cross-instance change signal (ADR-0024 decision 6). Every route, realm or
    /// certificate write stores a fresh random value here, and every instance watches it through the
    /// settings store's PostgreSQL <c>LISTEN/NOTIFY</c> channel and re-projects its route table and SNI
    /// map. Not a user setting — never offered in the UI, never listed among the proxy card's paths, and
    /// deliberately not env-pinnable: an environment variable would pin the value, so the one write that
    /// tells the other instances something changed would stop taking effect and their route tables would
    /// silently stop converging.
    /// </summary>
    public const string ProxyRoutesVersion = "Watchtower:Proxy:RoutesVersion";
    public const string ProxyAdminEmail = "Watchtower:Proxy:AdminEmail";
    public const string ProxyCaddyImage = "Watchtower:Proxy:CaddyImage";
    public const string ProxyYarpHttpPort = "Watchtower:Proxy:Yarp:HttpPort";
    public const string ProxyYarpHttpsPort = "Watchtower:Proxy:Yarp:HttpsPort";
    public const string ProxyYarpAcmeDirectoryUrl = "Watchtower:Proxy:Yarp:AcmeDirectoryUrl";
    public const string ProxyYarpAcmeCaBundlePath = "Watchtower:Proxy:Yarp:AcmeCaBundlePath";
    public const string ProxyYarpAcmeEabKeyId = "Watchtower:Proxy:Yarp:AcmeEabKeyId";
    public const string ProxyYarpAcmeEabHmacKey = "Watchtower:Proxy:Yarp:AcmeEabHmacKey";
    public const string ProxyYarpRedirectHttpToHttps = "Watchtower:Proxy:Yarp:RedirectHttpToHttps";
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

    // ── Backups (backups.updateConfig, ADR-0016) ─────────────────────────────
    public const string BackupEnabled = "Watchtower:Backup:Enabled";
    public const string BackupCron = "Watchtower:Backup:Cron";
    /// <summary>Legacy <c>HH:mm</c> alias of <see cref="BackupCron"/>; still honoured when set (env or stored).</summary>
    public const string BackupTime = "Watchtower:Backup:Time";
    public const string BackupMisfireGraceMinutes = "Watchtower:Backup:MisfireGraceMinutes";
    public const string BackupInstanceName = "Watchtower:Backup:InstanceName";
    public const string BackupRetentionDays = "Watchtower:Backup:RetentionDays";
    public const string BackupRetentionMaxCount = "Watchtower:Backup:RetentionMaxCount";
    public const string BackupEncryptionPassphrase = "Watchtower:Backup:EncryptionPassphrase";
    public const string BackupHelperImage = "Watchtower:Backup:HelperImage";
    public const string BackupProvider = "Watchtower:Backup:Provider";
    /// <summary>Whether the schedule also dumps Watchtower's own database (ADR-0027).</summary>
    public const string BackupIncludeSelf = "Watchtower:Backup:IncludeSelf";
    /// <summary>Explicit container for Watchtower's own PostgreSQL, when detection cannot pick one.</summary>
    public const string BackupSelfPostgresContainer = "Watchtower:Backup:SelfPostgresContainer";
    /// <summary>
    /// The instance self-backup's schedule cursor — the due time of the last window that was enqueued,
    /// the stackless counterpart of <see cref="Entities.Stack.LastScheduledBackupAt"/>. Written by the
    /// schedule tick, never offered in the UI.
    /// </summary>
    public const string BackupSelfLastScheduledAt = "Watchtower:Backup:SelfLastScheduledAt";

    /// <summary>
    /// The nonce an in-flight instance restore writes into the database it is about to replace
    /// (ADR-0027 §5). After the restart its <em>absence</em> is the proof that the replay committed —
    /// nothing else can remove it, because nothing else knows it. Never offered in the UI.
    /// </summary>
    public const string RestorePendingNonce = "Watchtower:Restore:PendingNonce";

    /// <summary>
    /// The post-restore recovery checklist (ADR-0027 §6), as JSON. A settings row because there is at
    /// most one, it has to survive the restart the restore itself causes, and a table for it would be a
    /// schema change carried by every instance that never restores anything.
    /// </summary>
    public const string RestoreRecovery = "Watchtower:Restore:Recovery";
    public const string BackupSftpHost = "Watchtower:Backup:Sftp:Host";
    public const string BackupSftpPort = "Watchtower:Backup:Sftp:Port";
    public const string BackupSftpUsername = "Watchtower:Backup:Sftp:Username";
    public const string BackupSftpPassword = "Watchtower:Backup:Sftp:Password";
    public const string BackupSftpPrivateKey = "Watchtower:Backup:Sftp:PrivateKey";
    public const string BackupSftpPrivateKeyPassphrase = "Watchtower:Backup:Sftp:PrivateKeyPassphrase";
    public const string BackupSftpBasePath = "Watchtower:Backup:Sftp:BasePath";
    public const string BackupLocalBasePath = "Watchtower:Backup:Local:BasePath";
}
