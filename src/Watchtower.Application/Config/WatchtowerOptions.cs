namespace Watchtower.Application.Config;

/// <summary>
/// Strongly-typed configuration options for Watchtower.
/// Bound from the "Watchtower" section of appsettings.json or environment variables
/// (e.g. WATCHTOWER__DBPATH, WATCHTOWER__DOCKERAPIVERSION).
/// </summary>
public sealed record WatchtowerOptions {
    /// <summary>Path to the SQLite database file.</summary>
    public string DbPath { get; init; } = "/data/watchtower.db";

    /// <summary>
    /// Docker Engine API version used for all Docker communication.
    /// <list type="bullet">
    ///   <item><description>
    ///     Direct API calls (<see cref="Services.DockerEngineClient"/>) use it as the URL segment: <c>/v1.43/containers/…</c>
    ///   </description></item>
    ///   <item><description>
    ///     <c>docker compose</c> subprocesses (<see cref="Services.ComposeCliService"/>) receive it via the
    ///     <c>DOCKER_API_VERSION</c> environment variable, preventing the compose CLI from
    ///     auto-negotiating a version newer than the daemon supports.
    ///   </description></item>
    /// </list>
    /// Update this if your Docker daemon supports a newer API version.
    /// </summary>
    public string DockerApiVersion { get; init; } = "1.43";

    /// <summary>
    /// Publicly reachable base URL of this Watchtower instance, e.g. <c>https://watchtower.example.com</c>
    /// (no trailing path). When set, every deploy injects it into the stack's environment as
    /// <c>WATCHTOWER_URL</c> so a deployed application knows where to reach the App API
    /// (<c>/api/app/*</c>) without hard-coding it. Optional: when unset the variable is simply not
    /// injected — set via <c>WATCHTOWER__PUBLICBASEURL</c> or appsettings.json.
    /// </summary>
    public string? PublicBaseUrl { get; init; }

    /// <summary>
    /// When true, a background service periodically checks for a newer Watchtower image
    /// so the UI badge stays up to date without a manual check.
    /// Set via <c>WATCHTOWER__AUTOCHECKENABLED=true</c> or appsettings.json.
    /// Defaults to false so no outbound registry traffic is generated unless opted in.
    /// </summary>
    public bool AutoCheckEnabled { get; init; } = false;

    /// <summary>
    /// How often the background auto-check runs, in minutes. Clamped to 1–1440.
    /// Only relevant when <see cref="AutoCheckEnabled"/> is true.
    /// </summary>
    public int AutoCheckIntervalMinutes { get; init; } = 5;

    /// <summary>
    /// When true, a background service periodically checks whether any container image in
    /// each stack has a newer version available in the registry.
    /// Set via <c>WATCHTOWER__STACKCHECKENABLED=true</c> or appsettings.json.
    /// Defaults to false so no outbound registry traffic is generated unless opted in.
    /// </summary>
    public bool StackCheckEnabled { get; init; } = false;

    /// <summary>
    /// How often the stack update background check runs, in minutes. Clamped to 1–1440.
    /// Only relevant when <see cref="StackCheckEnabled"/> is true.
    /// </summary>
    public int StackCheckIntervalMinutes { get; init; } = 15;

    /// <summary>
    /// When true, a background service periodically removes dangling (untagged) images — the
    /// equivalent of <c>docker image prune -f</c>, never <c>-a</c>, so tagged images a stack may
    /// still be rolled back to are left alone.
    /// Set via <c>WATCHTOWER__IMAGEPRUNEENABLED=true</c> or appsettings.json.
    /// Defaults to false so nothing is deleted from the host unless opted in.
    /// </summary>
    public bool ImagePruneEnabled { get; init; } = false;

    /// <summary>
    /// How often the dangling-image prune runs, in minutes. Clamped to 1–1440.
    /// Only relevant when <see cref="ImagePruneEnabled"/> is true.
    /// </summary>
    public int ImagePruneIntervalMinutes { get; init; } = 1440;

    /// <summary>
    /// Metrics backend selection and its optional InfluxDB reader settings (ADR-0007).
    /// Bound from <c>WATCHTOWER__METRICS__*</c> (e.g. <c>WATCHTOWER__METRICS__BACKEND=influxdb</c>,
    /// <c>WATCHTOWER__METRICS__INFLUX__URL=…</c>).
    /// </summary>
    public MetricsOptions Metrics { get; init; } = new();

    /// <summary>
    /// Reverse-proxy settings. Three providers (ADR-0015, ADR-0017): <c>yarp</c> — the in-process
    /// proxy Watchtower runs itself, and the default; <c>caddy</c> — a sibling Caddy container,
    /// deprecated and kept for existing installs; <c>cloudflare</c> — a Cloudflare Tunnel.
    /// Bound from <c>WATCHTOWER__PROXY__*</c> (e.g. <c>WATCHTOWER__PROXY__ENABLED=true</c>,
    /// <c>WATCHTOWER__PROXY__ADMINEMAIL=…</c>).
    /// </summary>
    public ProxyOptions Proxy { get; init; } = new();

    /// <summary>
    /// Self-hosted GitHub Actions runner settings (docs/ci-runners/design.md). Bound from
    /// <c>WATCHTOWER__CI__*</c> (e.g. <c>WATCHTOWER__CI__INSTANCENAME=nas</c>).
    /// </summary>
    public CiOptions Ci { get; init; } = new();

    /// <summary>
    /// Central authorization settings (docs/central-auth/design.md). Bound from
    /// <c>WATCHTOWER__AUTH__*</c> (e.g. <c>WATCHTOWER__AUTH__ENABLED=true</c>,
    /// <c>WATCHTOWER__AUTH__BOOTSTRAPPASSWORD=…</c>).
    /// </summary>
    public AuthOptions Auth { get; init; } = new();

    /// <summary>
    /// Stack backup settings (ADR-0016, docs/backups.md). Bound from <c>WATCHTOWER__BACKUP__*</c>
    /// (e.g. <c>WATCHTOWER__BACKUP__ENABLED=true</c>, <c>WATCHTOWER__BACKUP__SFTP__HOST=…</c>).
    /// </summary>
    public BackupOptions Backup { get; init; } = new();
}

/// <summary>
/// Settings for scheduled stack backups (ADR-0016): volume archives shipped to a pluggable storage
/// backend, optionally encrypted, with retention applied per stack. The feature itself is per-stack
/// (<see cref="Entities.Stack.BackupEnabled"/>); these are the instance-wide knobs. All of them are
/// runtime-editable through the settings store; env vars pin (ADR-0014).
/// </summary>
public sealed record BackupOptions {
    /// <summary>
    /// Master switch for the daily backup schedule. Off by default so no storage credentials are
    /// required and no containers are ever stopped unless the operator opts in. Manual
    /// <c>backups.run</c> works regardless of this switch (it still needs a configured provider).
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Server-local time of day ("HH:mm") the daily backup window opens — same semantics as
    /// <see cref="Entities.Stack.AutoDeployTime"/>. A window that passed while Watchtower was down
    /// is skipped, so a restart never backs up (or stops containers) outside the window.
    /// </summary>
    public string Time { get; init; } = "03:30";

    /// <summary>
    /// Name identifying this Watchtower instance in the remote layout
    /// (<c>{basePath}/{instance}/{stack}/…</c>) and in every backup manifest, so two instances can
    /// share one storage target without ambiguity. Defaults to the machine name — set it explicitly
    /// in containers, where the machine name is the container id and changes on recreate.
    /// </summary>
    public string? InstanceName { get; init; }

    /// <summary>Backups older than this many days are pruned after each successful run. 0 = keep forever.</summary>
    public int RetentionDays { get; init; } = 30;

    /// <summary>Keep at most this many backups per stack (oldest pruned first). 0 = unlimited.</summary>
    public int RetentionMaxCount { get; init; } = 0;

    /// <summary>
    /// When set, every backup is encrypted with AES-256-CBC in the OpenSSL <c>enc</c> container
    /// format (PBKDF2-SHA256, see <see cref="Services.BackupEncryption"/>), so restore needs nothing
    /// but stock OpenSSL. Treated as a secret — never logged, never echoed to the UI. Empty =
    /// unencrypted.
    /// </summary>
    public string? EncryptionPassphrase { get; init; }

    /// <summary>
    /// Image for the never-started helper container whose bind mounts expose the volumes to the
    /// Docker archive endpoint (ADR-0016 §1). Any locally available or pullable image works; it is
    /// pulled on first use.
    /// </summary>
    public string HelperImage { get; init; } = "busybox:stable";

    /// <summary>Storage backend the archives are shipped to: <c>sftp</c> (default) or <c>local</c>.</summary>
    public string Provider { get; init; } = "sftp";

    /// <summary>SFTP storage settings. Only used when <see cref="Provider"/> is <c>sftp</c>.</summary>
    public SftpBackupOptions Sftp { get; init; } = new();

    /// <summary>Local-directory storage settings. Only used when <see cref="Provider"/> is <c>local</c>.</summary>
    public LocalBackupOptions Local { get; init; } = new();

    /// <summary>The provider <see cref="Provider"/> resolves to (case-insensitive; unknown ⇒ <c>sftp</c>).</summary>
    public BackupProviderKind ResolveProvider() =>
        string.Equals(Provider, "local", StringComparison.OrdinalIgnoreCase)
            ? BackupProviderKind.Local
            : BackupProviderKind.Sftp;

    /// <summary>Resolved instance name: explicit setting or machine name.</summary>
    public string ResolveInstanceName() =>
        string.IsNullOrWhiteSpace(InstanceName) ? Environment.MachineName.ToLowerInvariant() : InstanceName.Trim();
}

/// <summary>The two backup storage backends (ADR-0016).</summary>
public enum BackupProviderKind {
    Sftp,
    Local,
}

/// <summary>
/// SFTP connection settings for the backup storage provider. Password and private-key auth may be
/// combined (both are offered to the server). A Hetzner Storage Box uses port 23 for SSH/SFTP.
/// </summary>
public sealed record SftpBackupOptions {
    /// <summary>SFTP host name, e.g. <c>u123456.your-storagebox.de</c>.</summary>
    public string? Host { get; init; }

    /// <summary>SSH port. 22 is the protocol default; Hetzner Storage Boxes use 23.</summary>
    public int Port { get; init; } = 22;

    /// <summary>SFTP user name.</summary>
    public string? Username { get; init; }

    /// <summary>Password, when using password auth. Treated as a secret — never logged, never echoed.</summary>
    public string? Password { get; init; }

    /// <summary>
    /// PEM-encoded private key, when using key auth (the full <c>-----BEGIN … KEY-----</c> block).
    /// Treated as a secret — never logged, never echoed.
    /// </summary>
    public string? PrivateKey { get; init; }

    /// <summary>Passphrase of <see cref="PrivateKey"/>, when the key is encrypted. Secret.</summary>
    public string? PrivateKeyPassphrase { get; init; }

    /// <summary>Remote base directory the layout is rooted in (created if missing).</summary>
    public string BasePath { get; init; } = "watchtower-backups";
}

/// <summary>
/// Local-directory storage for backups: a path inside the container, i.e. an operator-mounted
/// second disk or network share. Also the provider the integration tests exercise.
/// </summary>
public sealed record LocalBackupOptions {
    /// <summary>Directory the layout is rooted in (created if missing).</summary>
    public string BasePath { get; init; } = "/backups";
}

/// <summary>
/// When the login cookie carries the <c>Secure</c> attribute.
/// </summary>
public enum AuthCookieSecurePolicy {
    /// <summary>
    /// Decide per request from whether it arrived over TLS — after <c>X-Forwarded-Proto</c> has been
    /// applied, so a request that reached a TLS-terminating proxy over HTTPS counts as secure. The right
    /// answer for both shipped topologies (published plain-HTTP port, and behind the proxy).
    /// </summary>
    Auto,

    /// <summary>
    /// Always set <c>Secure</c>. Use when Watchtower is only ever reached over HTTPS and the proxy does
    /// <em>not</em> send <c>X-Forwarded-Proto</c>, which would otherwise leave <see cref="Auto"/> issuing
    /// cookies without the flag. A cookie set this way is never sent over plain HTTP, so the published
    /// port stops working as a recovery path.
    /// </summary>
    Always,

    /// <summary>
    /// Never set <c>Secure</c>. Only for a deployment with no TLS anywhere — a lab or an isolated LAN.
    /// The session cookie then travels in the clear and is trivially interceptable; do not use it on an
    /// instance reachable from an untrusted network.
    /// </summary>
    Never,
}

/// <summary>
/// Settings for the central authorization plane: local user accounts, the login session, and the
/// per-route access policy the reverse proxy enforces (docs/central-auth/design.md).
/// Disabled by default so upgrading an existing deployment cannot lock its operator out.
/// </summary>
public sealed record AuthOptions {
    /// <summary>
    /// When true, Watchtower manages users and enforces access policy. Turning it on bootstraps an
    /// <c>admin</c> account (see <see cref="BootstrapPassword"/>). Set via
    /// <c>WATCHTOWER__AUTH__ENABLED=true</c>.
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Public hostname of the central login page, e.g. <c>watchtower.example.com</c>. Protected apps
    /// redirect unauthenticated visitors here, so it must be reachable through the proxy. Optional
    /// while only Watchtower's own UI is protected.
    /// </summary>
    public string? Host { get; init; }

    /// <summary>Idle lifetime of a login session in hours; each request slides it forward.</summary>
    public int SessionLifetimeHours { get; init; } = 12;

    /// <summary>Hard cap on a session's age in days, regardless of activity — after this a fresh login is required.</summary>
    public int AbsoluteSessionLifetimeDays { get; init; } = 7;

    /// <summary>
    /// Whether the login cookie is marked <c>Secure</c>. The default (<see cref="AuthCookieSecurePolicy.Auto"/>)
    /// follows the request scheme once <c>X-Forwarded-Proto</c> has been applied, which is correct for both
    /// shipped topologies; see <see cref="AuthCookieSecurePolicy"/> for when to override it. Set via
    /// <c>WATCHTOWER__AUTH__COOKIESECURE=Always</c>.
    /// </summary>
    public AuthCookieSecurePolicy CookieSecure { get; init; } = AuthCookieSecurePolicy.Auto;

    /// <summary>
    /// Directory holding the identity-token signing key and the data-protection keys. Must live on a
    /// persistent volume: losing it signs everyone out on restart.
    /// </summary>
    public string KeyPath { get; init; } = "/data/auth-keys";

    /// <summary>
    /// How many <c>POST /api/auth/login</c> attempts one client IP may make per minute before the
    /// endpoint answers <c>429</c> — a coarse backstop layered on top of the per-account Identity
    /// lockout (docs/central-auth/design.md §9). Set via <c>WATCHTOWER__AUTH__LOGINRATELIMITPERMINUTE</c>.
    /// A value below 1 is treated as 1 so a mistyped 0 cannot silently disable the backstop.
    /// <para>
    /// The partition is the <em>connection</em> remote IP, not <c>X-Forwarded-For</c> (which Watchtower
    /// deliberately does not process, see the forwarded-headers note in <c>Program.cs</c>): behind the
    /// single reverse proxy every request shares Caddy's address, so the limit is effectively
    /// instance-global there; on the published port it is genuinely per-client. Raise it on a busy
    /// multi-user instance reached through the proxy.
    /// </para>
    /// </summary>
    public int LoginRateLimitPerMinute { get; init; } = 10;

    /// <summary>
    /// Password for the <c>admin</c> account created on first start. A value configured here is a
    /// secret and is never written to the log. When it is left unset a random password is generated
    /// instead, and <em>that</em> one is logged once — it has no other way of reaching the operator.
    /// Ignored when <see cref="ResetPassword"/> is also set on a fresh database: recovery runs first
    /// and creates the account, so <see cref="ResetPassword"/> wins.
    /// </summary>
    public string? BootstrapPassword { get; init; }

    /// <summary>
    /// Break-glass recovery: when set, every start guarantees an <c>admin</c> account whose password is
    /// this value and which is not locked out — recreating the account if it was renamed or deleted.
    /// Takes precedence over <see cref="BootstrapPassword"/>, including on a fresh database.
    /// Remove it again once you are back in. Treated as a secret; never logged.
    /// </summary>
    public string? ResetPassword { get; init; }
}

/// <summary>
/// Settings for the ephemeral GitHub Actions runners this instance manages. The feature itself is
/// per-repo (a repo enabled via <c>ci.addRepo</c> gets runners); these are the instance-wide knobs.
/// </summary>
public sealed record CiOptions {
    /// <summary>
    /// Name identifying this Watchtower instance in runner names and labels
    /// (<c>watchtower-{instance}-…</c>). Defaults to the machine hostname.
    /// </summary>
    public string? InstanceName { get; init; }

    /// <summary>Default runner image; per-repo override via <see cref="Entities.CiRepo.RunnerImage"/>.</summary>
    public string RunnerImage { get; init; } = "ghcr.io/actions/actions-runner:latest";

    /// <summary>Reconcile loop interval in seconds. Clamped to 5–300.</summary>
    public int ReconcileIntervalSeconds { get; init; } = 15;

    /// <summary>Resolved instance name: explicit setting or machine hostname.</summary>
    public string ResolveInstanceName() =>
        string.IsNullOrWhiteSpace(InstanceName) ? Environment.MachineName.ToLowerInvariant() : InstanceName.Trim();
}

/// <summary>
/// Settings for the reverse-proxy plane. Three providers exist — see ADR-0015 and ADR-0017:
/// <list type="bullet">
///   <item><description>
///     <b><c>yarp</c></b> — the <b>in-process</b> proxy and the default: Watchtower terminates the
///     ingress ports itself and issues its own certificates over ACME, with no sibling container and
///     no control network at all.
///   </description></item>
///   <item><description>
///     <b><c>caddy</c></b> — <b>deprecated</b> (ADR-0017), kept for existing installs: Watchtower
///     manages a sibling Caddy container that publishes host ports 80/443 with automatic TLS.
///   </description></item>
///   <item><description>
///     <b><c>cloudflare</c></b> — a <b>Cloudflare Tunnel</b>: routes are projected into a cloudflared
///     tunnel's ingress rules + DNS via the Cloudflare API — no host ports, no ACME.
///   </description></item>
/// </list>
/// All three project the same <c>Route</c> table.
/// Disabled by default so nothing binds ports or spawns containers unless the operator opts in.
/// </summary>
public sealed record ProxyOptions {
    /// <summary>
    /// When true, the selected <see cref="Provider"/> reconciles routes. Set via
    /// <c>WATCHTOWER__PROXY__ENABLED=true</c> or Settings → Reverse proxy (runtime-switchable).
    /// </summary>
    public bool Enabled { get; init; } = false;

    /// <summary>
    /// Which proxy backend serves the routes: <c>yarp</c> (the in-process proxy, default),
    /// <c>caddy</c> (deprecated) or <c>cloudflare</c>. Unknown values resolve to <c>yarp</c>.
    /// Runtime-switchable — switching tears the old provider's data plane down and reconciles the new
    /// one. An instance that already served routes under the pre-ADR-0017 implicit <c>caddy</c>
    /// default is pinned to <c>caddy</c> once at startup by
    /// <see cref="Services.ProxyProviderMigration"/>, so an upgrade never switches providers silently.
    /// </summary>
    public string Provider { get; init; } = "yarp";

    /// <summary>
    /// Email registered with the ACME CA (Let's Encrypt/ZeroSSL) for expiry notices. Optional but
    /// recommended. When empty, certificates are issued without an account email. Read by both
    /// certificate-issuing providers — Caddy and the in-process proxy; ignored by Cloudflare, whose
    /// edge terminates TLS.
    /// </summary>
    public string? AdminEmail { get; init; }

    /// <summary>
    /// Caddy image to run. Defaults to the official <c>caddy:2</c>. Caddy only — and Caddy is
    /// deprecated (ADR-0017).
    /// </summary>
    public string CaddyImage { get; init; } = "caddy:2";

    /// <summary>In-process proxy settings. Only used when <see cref="Provider"/> is <c>yarp</c>.</summary>
    public YarpProxyOptions Yarp { get; init; } = new();

    /// <summary>Cloudflare Tunnel settings. Only used when <see cref="Provider"/> is <c>cloudflare</c>.</summary>
    public CloudflareProxyOptions Cloudflare { get; init; } = new();

    /// <summary>
    /// The provider <see cref="Provider"/> resolves to (case-insensitive; unknown or blank ⇒
    /// <c>yarp</c>, the default since ADR-0017).
    /// </summary>
    public ProxyProviderKind ResolveProvider() {
        var provider = Provider?.Trim() ?? "";
        if (string.Equals(provider, ProxyProviderNames.Caddy, StringComparison.OrdinalIgnoreCase))
            return ProxyProviderKind.Caddy;
        if (string.Equals(provider, ProxyProviderNames.Cloudflare, StringComparison.OrdinalIgnoreCase))
            return ProxyProviderKind.Cloudflare;
        return ProxyProviderKind.Yarp;
    }

    /// <summary>The canonical wire name of the resolved provider — what the API surfaces and stores.</summary>
    public string ProviderName() => ProxyProviderNames.From(ResolveProvider());
}

/// <summary>The reverse-proxy backends. See ADR-0015 and ADR-0017.</summary>
public enum ProxyProviderKind {
    /// <summary>A sibling Caddy container on host ports 80/443. Deprecated by ADR-0017.</summary>
    Caddy,
    Cloudflare,
    /// <summary>
    /// The in-process reverse proxy — the default since ADR-0017: Watchtower terminates 80/443 itself,
    /// no sibling container.
    /// </summary>
    Yarp,
}

/// <summary>
/// The canonical wire names of the proxy providers — the strings <c>Proxy:Provider</c> is stored as
/// and the API surfaces. One place so the handlers, the validation message and the settings writer
/// cannot drift apart.
/// </summary>
public static class ProxyProviderNames {
    public const string Caddy = "caddy";
    public const string Cloudflare = "cloudflare";
    public const string Yarp = "yarp";

    /// <summary>
    /// Every accepted provider name, in the order the Settings page offers them — the default first,
    /// the deprecated one second.
    /// </summary>
    public static readonly string[] All = [Yarp, Caddy, Cloudflare];

    /// <summary>The wire name of a resolved provider kind.</summary>
    public static string From(ProxyProviderKind kind) => kind switch {
        ProxyProviderKind.Caddy => Caddy,
        ProxyProviderKind.Cloudflare => Cloudflare,
        _ => Yarp,
    };
}

/// <summary>
/// In-process reverse proxy + ACME settings (<c>WATCHTOWER__PROXY__YARP__*</c>), per ADR-0017. Used only
/// when <see cref="ProxyOptions.Provider"/> is <c>yarp</c>: Watchtower binds 80/443 itself, forwards
/// to the routed containers over the per-stack ingress networks, and obtains its own certificates
/// from an ACME CA — so there is neither a sibling proxy container nor a control network.
/// </summary>
public sealed record YarpProxyOptions {
    /// <summary>
    /// Directory holding the issued PEM certificates and the ACME account key. Read at bind time
    /// (the directory is created at startup and the certificate store is opened over it), so it is
    /// environment/appsettings-only — not editable from the Settings page.
    /// </summary>
    public string CertPath { get; init; } = "/data/proxy-certs";

    /// <summary>
    /// ACME directory URL of the CA that issues the certificates. Defaults to Let's Encrypt
    /// production; point it at the staging directory while testing, or at an internal CA's directory.
    /// </summary>
    public string AcmeDirectoryUrl { get; init; } = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>
    /// Path to a PEM bundle of roots trusted <em>in addition</em> to the system trust store when
    /// talking to the ACME directory. The escape hatch for an internal CA (e.g. step-ca) whose root
    /// the container does not ship. Optional; unset means system trust only.
    /// </summary>
    public string? AcmeCaBundlePath { get; init; }

    /// <summary>
    /// External Account Binding key id, for CAs that require an account to be bound to an existing
    /// customer record (ZeroSSL, Sectigo, many internal CAs). Set together with
    /// <see cref="AcmeEabHmacKey"/> or not at all.
    /// </summary>
    public string? AcmeEabKeyId { get; init; }

    /// <summary>
    /// External Account Binding HMAC key, base64url-encoded as the CA hands it out. Treated as a
    /// secret — never logged, never echoed to the UI (the config surface reports only whether one is
    /// stored).
    /// </summary>
    public string? AcmeEabHmacKey { get; init; }

    /// <summary>
    /// How many certificate orders may be in flight at once. Kept low on purpose: ACME CAs rate-limit
    /// per account, and a first start with many routes would otherwise burn the budget in one burst.
    /// Environment/appsettings only.
    /// </summary>
    public int AcmeMaxConcurrentOrders { get; init; } = 2;

    /// <summary>
    /// When true (default), the challenge responder is probed over the public hostname before the
    /// order is submitted, so a domain whose DNS does not point here fails fast as
    /// <see cref="Entities.RouteStatus.AwaitingDns"/> instead of consuming an ACME failure.
    /// Environment only — an operator behind a split-horizon DNS may need it off.
    /// </summary>
    public bool AcmeSelfCheckEnabled { get; init; } = true;

    /// <summary>
    /// When true (default), plain HTTP requests for a TLS route are redirected to HTTPS. Turn it off
    /// when another TLS terminator (a load balancer, a cloud ingress) fronts Watchtower and already
    /// speaks HTTPS to the client — redirecting again would loop.
    /// </summary>
    public bool RedirectHttpToHttps { get; init; } = true;
}

/// <summary>
/// Cloudflare Tunnel provider settings (<c>WATCHTOWER__PROXY__CLOUDFLARE__*</c>). Watchtower projects
/// the route table into the tunnel's ingress rules (public hostname → service) and upserts a proxied
/// CNAME per route domain; TLS terminates at Cloudflare's edge.
/// </summary>
public sealed record CloudflareProxyOptions {
    /// <summary>Cloudflare account id owning the tunnel.</summary>
    public string? AccountId { get; init; }

    /// <summary>Zone id of the domain the route hostnames live under (single-zone by design for now).</summary>
    public string? ZoneId { get; init; }

    /// <summary>
    /// API token with <c>Cloudflare Tunnel:Edit</c> and <c>DNS:Edit</c> (Zero Trust Access scopes come
    /// with phase 3). Treated as a secret — never logged, never echoed to the UI.
    /// </summary>
    public string? ApiToken { get; init; }

    /// <summary>Name of the remotely-managed tunnel Watchtower configures. Found (or created, when
    /// <see cref="Managed"/>) by name on every reconcile, so no local state is kept.</summary>
    public string TunnelName { get; init; } = "watchtower";

    /// <summary>
    /// Your Zero Trust team — the bare name (<c>myteam</c>) or the full host
    /// (<c>myteam.cloudflareaccess.com</c>). Used to derive the Access JWKS URL injected into deploys
    /// as <c>WATCHTOWER_AUTH_JWKS_URL</c> so apps verify <c>Cf-Access-Jwt-Assertion</c> without
    /// hard-coding the issuer. Optional; without it the variable is simply not injected.
    /// </summary>
    public string? TeamDomain { get; init; }

    /// <summary>
    /// When true (default), Watchtower runs <c>cloudflared</c> itself as a managed container — created,
    /// supervised and torn down over the Docker socket, exactly like the Caddy container. When false,
    /// the operator runs cloudflared (anywhere), and Watchtower only manages the tunnel's remote
    /// configuration and DNS; see <see cref="CloudflaredContainerName"/> for the network hookup.
    /// </summary>
    public bool Managed { get; init; } = true;

    /// <summary>cloudflared image for the managed container.</summary>
    public string CloudflaredImage { get; init; } = "cloudflare/cloudflared:latest";

    /// <summary>
    /// Unmanaged mode only: the name of the operator-run cloudflared container on this Docker host.
    /// When set, Watchtower connects it to the per-stack ingress networks so the generated
    /// <c>http://{project}-{service}:{port}</c> ingress URLs resolve — but never creates, updates or
    /// removes it. Leave empty if cloudflared runs elsewhere and you route to services yourself.
    /// </summary>
    public string? CloudflaredContainerName { get; init; }

    /// <summary>
    /// Comma-separated emails allowed through the Zero Trust Access application of every
    /// <see cref="Entities.AccessMode.Authenticated"/> route (phase 3 of ADR-0015). Restricted routes
    /// derive their allow-list from the route's grants instead. Requires the API token to also carry
    /// <c>Access: Apps and Policies:Edit</c>.
    /// </summary>
    public string AccessAllowedEmails { get; init; } = "";

    /// <summary>
    /// Comma-separated email domains (e.g. <c>example.com</c>) allowed through the Access application
    /// of every <see cref="Entities.AccessMode.Authenticated"/> route, alongside
    /// <see cref="AccessAllowedEmails"/>.
    /// </summary>
    public string AccessAllowedEmailDomains { get; init; } = "";

    /// <summary>
    /// Comma-separated Zero Trust <b>Access group</b> ids (UUIDs) admitted by every Authenticated
    /// route's Access application. The natural fit when the allow-list already lives in a Cloudflare
    /// Access group (e.g. your Entra ID users) — Watchtower references the group instead of
    /// maintaining a parallel email list.
    /// </summary>
    public string AccessGroupIds { get; init; } = "";

    /// <summary>
    /// Comma-separated <b>reusable Access policy</b> ids attached to every Authenticated route's
    /// Access application, for accounts whose default allow policy already exists in the dashboard.
    /// Attached alongside (not instead of) any Watchtower-generated app policy from the email/domain/
    /// group settings above.
    /// </summary>
    public string AccessReusablePolicyIds { get; init; } = "";

    /// <summary>Parses a comma/semicolon/whitespace-separated list into trimmed, distinct entries.</summary>
    public static string[] SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value
                .Split([',', ';', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}

/// <summary>
/// Selects where the <c>metrics.*</c> handlers read from (ADR-0007, amended by ADR-0013). The choice
/// is resolved per call through <c>IOptionsMonitor</c> and is runtime-switchable — persisting these
/// keys through the settings store (the <c>metrics.updateConfig</c> handler) re-binds them live, no
/// restart. The sampler reads the backend each tick, so exactly one collector stays active.
/// </summary>
public sealed record MetricsOptions {
    /// <summary>
    /// <c>sqlite</c> (default) — the in-memory live ring plus SQLite-persisted history with windowed
    /// retention; <c>memory</c> — the live ring only, nothing written; or <c>influxdb</c> — read from
    /// an InfluxDB an external collector populates, with the sampler idle so there is a single
    /// collector. Unknown values resolve to <c>sqlite</c>, matching the default.
    /// </summary>
    public string Backend { get; init; } = "sqlite";

    /// <summary>
    /// How many days of history the <c>sqlite</c> backend keeps (its rollup tier — see ADR-0013).
    /// Clamped to 1–365 where it is consumed. Ignored by the other backends.
    /// </summary>
    public int RetentionDays { get; init; } = 30;

    /// <summary>InfluxDB connection + schema mapping. Only used when <see cref="Backend"/> is <c>influxdb</c>.</summary>
    public InfluxOptions Influx { get; init; } = new();

    /// <summary>The backend <see cref="Backend"/> resolves to (case-insensitive; unknown ⇒ <c>sqlite</c>).</summary>
    public MetricsBackendKind ResolveBackend() =>
        string.Equals(Backend, "memory", StringComparison.OrdinalIgnoreCase) ? MetricsBackendKind.Memory
        : string.Equals(Backend, "influxdb", StringComparison.OrdinalIgnoreCase) ? MetricsBackendKind.Influxdb
        : MetricsBackendKind.Sqlite;

    /// <summary>True when <see cref="Backend"/> selects the InfluxDB reader (case-insensitive).</summary>
    public bool UsesInflux => ResolveBackend() == MetricsBackendKind.Influxdb;
}

/// <summary>The three metrics backends (ADR-0013): live-only, SQLite-persisted (default), BYO InfluxDB.</summary>
public enum MetricsBackendKind {
    Memory,
    Sqlite,
    Influxdb,
}

/// <summary>
/// InfluxDB v2 connection and schema-mapping settings for the InfluxDB metrics backend. The schema
/// defaults track the OpenTelemetry <c>docker_stats</c>/<c>hostmetrics</c> semantic conventions the
/// collector emits; only <see cref="ComposeProjectTag"/> commonly needs changing (it depends on how the
/// collector was told to promote the compose-project label — see ADR-0007).
/// </summary>
public sealed record InfluxOptions {
    /// <summary>Base URL of the InfluxDB v2 server, e.g. <c>http://influxdb:8086</c>.</summary>
    public string? Url { get; init; }

    /// <summary>InfluxDB v2 organization the bucket belongs to.</summary>
    public string? Org { get; init; }

    /// <summary>Bucket the collector writes metrics into.</summary>
    public string? Bucket { get; init; }

    /// <summary>API token with read access to <see cref="Bucket"/>. Treated as a secret — never logged.</summary>
    public string? Token { get; init; }

    /// <summary>
    /// Tag name carrying the compose project, used for the per-stack rollup. <b>Opt-in: empty by
    /// default</b>, because referencing a tag the collector doesn't emit is a hard Flux error. Set this
    /// only after telling the collector to promote the compose-project label (docker_stats
    /// <c>container_labels_to_metric_labels: { com.docker.compose.project: compose_project }</c>), then
    /// set this to <c>compose_project</c>. Empty ⇒ per-stack rollup is empty (per-container and host still
    /// work).
    /// </summary>
    public string ComposeProjectTag { get; init; } = "";

    /// <summary>
    /// Filesystem mount point reported for the host-disk cell, matched against the
    /// <c>system.filesystem.usage</c> <c>mountpoint</c> tag. Defaults to <c>/</c> (the conventional root,
    /// matching the in-memory backend). On multi-volume hosts (e.g. Synology, where <c>/</c> is a small
    /// system partition) point this at the data volume, e.g. <c>/volume2</c>. Unmatched ⇒ disk
    /// unavailable.
    /// </summary>
    public string DiskMountpoint { get; init; } = "/";
}
