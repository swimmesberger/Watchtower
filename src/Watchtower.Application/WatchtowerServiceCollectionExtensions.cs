using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;
using Elarion.Authorization;
using Elarion.Identity;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application;

/// <summary>
/// Registers Watchtower's application-layer infrastructure: strongly-typed options, the SQLite
/// EF Core context, the Docker/compose/git service layer, the deploy engine, and the optional
/// background update checkers. Elarion handlers and modules are registered separately via
/// <c>AddElarion</c> in the host.
/// </summary>
public static class WatchtowerServiceCollectionExtensions {
    public static IServiceCollection AddWatchtowerServices(this IServiceCollection services, IConfiguration config) {
        var section = config.GetSection("Watchtower");
        services.Configure<WatchtowerOptions>(section);

        // Wall-clock seam. Session expiry is the one place where "now" is a correctness decision rather
        // than a log line, so it is injected and the tests can move it.
        services.TryAddSingleton(TimeProvider.System);

        // Which settings are pinned by WATCHTOWER__* env vars (env wins over the settings store — see the
        // configuration layering in Program.cs). TryAdd so tests can substitute a fake environment.
        services.TryAddSingleton<EnvironmentSettingPins>();

        var dbPath = section.GetValue<string>("DbPath") ?? "/data/watchtower.db";
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        services.AddDbContext<WatchtowerDbContext>(o =>
            o.UseSqlite($"Data Source={dbPath}")
             .UseSnakeCaseNamingConvention());

        // Stateless infrastructure (no DB) — singletons.
        services.AddSingleton<DockerEngineClient>();
        services.AddSingleton<ComposeCliService>();
        services.AddSingleton<GitCloneService>();
        services.AddSingleton<DeployOutputBroadcaster>();
        // Watchtower's own compose project name — resolved once, then cached — so no stack can be
        // created under it and read Watchtower's own containers through the App API.
        services.AddSingleton<SelfProjectNameProvider>();

        // Scoped data-access helpers (wrap the scoped DbContext).
        services.AddScoped<RegistryAuthBuilder>();
        // Realms (docs/central-auth/design.md §13). The resolver is the one place a host, a route or a
        // configuration value is turned into a population; the context is which population the current
        // request's credential lookups may see, and defaults to the operator realm so nothing that predates
        // realms changes behaviour. Both scoped — they read through the scoped context.
        services.AddScoped<RealmResolver>();
        services.AddScoped<IRealmContext, RealmContext>();
        // Public App API (/api/app/*): token auth + the read models the host endpoints translate.
        // Scoped because it reads through the scoped DbContext; the deploy queue resolves it from a
        // short-lived scope when it needs to materialize a stack's token.
        services.AddScoped<AppApiService>();
        // Tenant lifecycle, shared by the operator-facing templates.* handlers and the public
        // management API so both provision and tear down through one code path.
        services.AddScoped<TenantProvisioningService>();
        services.AddScoped<TenantTeardownService>();
        // Public management API (/api/mgmt/*): App API token auth + grant resolution + the tenant
        // read models the host endpoints translate.
        services.AddScoped<MgmtApiService>();
        // User-scoped tenant discovery behind both public surfaces' /tenants/accessible endpoints: verifies
        // the forwarded identity assertion against the calling stack's own domains, then filters the
        // template's tenants by what that user may enter.
        services.AddScoped<TenantDiscoveryService>();

        // Elarion settings — typed key/value store backed by the EF Setting entity. Replaces the
        // hand-rolled SettingsStore; used for self-update config/runtime state and the runtime-editable
        // automation toggles.
        services.AddElarionSettings();
        services.AddElarionSettingsEntityFrameworkCore<WatchtowerDbContext>();

        // Deploy queue — singleton for enqueuing; hosted for graceful shutdown.
        services.AddSingleton<DeployQueueService>();
        services.AddHostedService(sp => sp.GetRequiredService<DeployQueueService>());

        // Self-update — singleton + hosted so an in-progress apply is reconciled on startup and
        // cancelled cleanly on shutdown.
        services.AddSingleton<SelfUpdateService>();
        services.AddHostedService(sp => sp.GetRequiredService<SelfUpdateService>());

        // Reverse proxy (ADR-0015) — two providers behind one runtime router, mirroring the metrics
        // backend (ADR-0007): Caddy (host ports 80/443, automatic TLS) and Cloudflare Tunnel
        // (cloudflared + the Cloudflare API). Both are registered unconditionally and hosted so the
        // active one reconciles on startup (each self-gates on Proxy:Enabled + Proxy:Provider);
        // consumers inject IProxyProvider and the router resolves the selected backend per call, which
        // is what makes the provider switchable from the Settings page without a restart.
        // The general audit trail (audit.listEvents). Singleton — its writers are the singleton
        // providers; TryAdd so tests can substitute a recording double.
        services.TryAddSingleton<AuditLog>();
        services.AddSingleton<ProxyIngressNetworks>();
        services.AddSingleton<CaddyManager>();
        services.AddHostedService(sp => sp.GetRequiredService<CaddyManager>());
        services.AddSingleton<CloudflareApiClient>();
        services.AddSingleton<CloudflareTunnelProvider>();
        services.AddHostedService(sp => sp.GetRequiredService<CloudflareTunnelProvider>());
        services.AddSingleton<IProxyProvider, ProxyProviderRouter>();

        services.AddSingleton<StackUpdateService>();
        // Clears cached update flags for stacks an operator updated by hand, off the read path and
        // without touching a registry. Singleton because the per-stack debounce is process state.
        services.AddSingleton<StackUpdateRevalidator>();

        // Central authorization (docs/central-auth/design.md) — ASP.NET Identity *core* only
        // (UserManager + password hasher + lockout/security-stamp), stored through WatchtowerDbContext.
        // Registered unconditionally: nothing runs until something asks for a UserManager, and the
        // bootstrap below no-ops while Auth:Enabled is false.
        //
        // Data protection is REQUIRED here, not a convenience: the host builds with
        // WebApplication.CreateSlimBuilder, which registers none of it. It encrypts the password-reset
        // tokens today and the session cookies from the next work item on. The key ring is persisted to
        // Auth:KeyPath — unconditionally, because the default location is per-user and the shipped
        // container has no home directory, which would make the keys ephemeral and sign everyone out on
        // every restart. Directory created up front, exactly as DbPath's is above.
        var keyPath = section.GetValue<string>("Auth:KeyPath");
        if (string.IsNullOrWhiteSpace(keyPath)) keyPath = new AuthOptions().KeyPath;
        Directory.CreateDirectory(keyPath);
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        services.AddIdentityCore<User>(o => {
            // Brute-force protection: 5 failed logins park the account for 15 minutes.
            o.Lockout.AllowedForNewUsers = true;
            o.Lockout.MaxFailedAccessAttempts = 5;
            o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            // Length over composition rules — forced symbol classes push operators towards
            // predictable substitutions without adding real entropy.
            o.Password.RequiredLength = 10;
            o.Password.RequiredUniqueChars = 1;
            o.Password.RequireDigit = false;
            o.Password.RequireLowercase = false;
            o.Password.RequireUppercase = false;
            o.Password.RequireNonAlphanumeric = false;
            o.User.RequireUniqueEmail = false;
        })
        .AddUserStore<WatchtowerUserStore>()
        // Two of the providers AddDefaultTokenProviders would bring, and deliberately only those two.
        // The data protector backs password resets (including the break-glass hook), which go through a
        // token so the validators run before the stored hash is touched; the authenticator provider is
        // Identity's own RFC 6238 TOTP implementation, which is what verifies the codes an authenticator
        // app produces — no third-party TOTP package is involved. The phone and email providers have
        // nothing to drive them (Watchtower sends neither), so they stay out.
        .AddTokenProvider<DataProtectorTokenProvider<User>>(TokenOptions.DefaultProvider)
        .AddTokenProvider<AuthenticatorTokenProvider<User>>(TokenOptions.DefaultAuthenticatorProvider);

        // Login sessions (design.md §4): revocable database rows behind the __wt_sso cookie. Scoped, like
        // the context it writes through. Registered unconditionally — it is inert until something logs in.
        services.AddScoped<AuthSessionService>();

        // Two-factor (TOTP + recovery codes, design.md §4). Scoped, like the UserManager and the context it
        // writes through. Registered unconditionally and inert until an account enrols.
        services.AddScoped<UserMfaService>();

        // The ES256 signer behind X-Watchtower-Jwt and the JWKS endpoint (design.md §2.3). Singleton
        // because the key pair is process-wide state: loading it per request would re-read the PEM on
        // every proxied request, and generating it per request would produce a different `kid` each time.
        // Registered unconditionally and lazily — the key file is not touched until the first assertion
        // is minted or the JWKS is fetched, so a deployment with Auth:Enabled off never creates one.
        services.AddSingleton<AuthTokenSigner>();

        // Who the caller is, and therefore what [assembly: ElarionAuthorizationDefaults] lets through.
        // Registered BEFORE AddElarionClaimsCurrentUser on purpose: that helper uses TryAdd for
        // ICurrentUser, so registering first is how a host substitutes its own snapshot.
        var authEnabled = section.GetValue<bool>("Auth:Enabled");
        // Snapshot the mode the process is actually starting in: Auth:Enabled decides pipeline shape and
        // is not runtime-switchable, so the auth settings handlers report "restart required" against this.
        services.AddSingleton(new AuthStartupState(authEnabled));
        if (authEnabled) {
            services.AddScoped<WatchtowerClaimsCurrentUser>();
            services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<WatchtowerClaimsCurrentUser>());
            // Claim types must match what WatchtowerSessionAuthenticationHandler mints; both sides read
            // the WatchtowerClaims constants rather than repeating the strings.
            services.AddElarionClaimsCurrentUser(o => {
                o.UserIdClaimType = WatchtowerClaims.UserId;
                o.EmailClaimType = WatchtowerClaims.Email;
                o.RoleClaimType = WatchtowerClaims.Role;
            });
        } else {
            // No authentication configured ⇒ the local operator is the administrator, exactly as before.
            services.AddSingleton<ICurrentUser, ImplicitAdminCurrentUser>();
        }

        // The IAuthorizer the generated authorization decorator resolves. Required in BOTH modes: the
        // decorator is attached at compile time by [assembly: ElarionAuthorizationDefaults], so a missing
        // registration would fail every handler at resolution time rather than fail open.
        services.AddElarionAuthorization();

        // …and then decorated, so the management surface is the operator population's (design.md §13).
        // The framework's own ClaimsAuthorizer keeps evaluating each handler's declared requirements; the
        // realm rule is layered on top of it centrally rather than repeated as an attribute per handler,
        // because a rule that has to be repeated is one a new handler can be written without. Registered by
        // replacement rather than in front of AddElarionAuthorization: this must win regardless of whether
        // the framework registers its authorizer with Add or TryAdd.
        services.RemoveAll<IAuthorizer>();
        services.AddScoped<ClaimsAuthorizer>();
        services.AddScoped<IAuthorizer, SystemRealmAuthorizer>();

        // First-run admin + break-glass password reset. No-op unless Auth:Enabled.
        services.AddHostedService<AuthBootstrapService>();

        // CI runners (docs/ci-runners/design.md) — the orchestrator reconciles ephemeral GitHub
        // Actions runner containers for enabled repos; singleton so ci.* handlers can read live
        // status and wake it after config changes. Idle cost with no repos configured: one SQLite
        // query + one Docker label query per pass.
        services.AddSingleton<GitHubApiClient>();
        services.AddSingleton<CiRunnerOrchestrator>();
        services.AddHostedService(sp => sp.GetRequiredService<CiRunnerOrchestrator>());
        // Toolchain detection piggybacks on deploy clones; the recorder persists the profile and
        // wakes the orchestrator so the toolcache warmer converges (docs/ci-runners/design.md).
        services.AddSingleton<CiToolchainRecorder>();

        // Metrics backend (ADR-0007, amended by ADR-0013) — three backends behind one runtime router:
        // "sqlite" (default) persists windowed history next to the live ring, "memory" keeps the ring
        // only, "influxdb" reads an externally-collected InfluxDB. Everything is registered
        // unconditionally; the router resolves the backend from IOptionsMonitor per call and the sampler
        // re-checks it per tick (idling under influxdb so exactly one collector runs). That is what
        // makes the backend switchable from the Settings page without a restart.
        services.AddSingleton<MetricsStore>();
        services.AddSingleton<MetricsPersistenceService>();
        services.AddHostedService<MetricsSampler>();
        services.AddSingleton<InMemoryMetricsSource>();
        services.AddSingleton<SqliteMetricsSource>();
        services.AddSingleton<IMetricsSource, MetricsSourceRouter>();

        // Client-exposed feature flags (ADR-0030): the session bootstrap evaluates every module's
        // [ClientFeatures] names through this one service, per call — "metrics-history" follows the
        // routed metrics backend above (including across a runtime switch), "apps-portal" reflects the
        // caller's realm. Scoped because the second of those reads ICurrentUser; a singleton could only
        // answer the deployment-scoped half.
        services.AddScoped<IFeatureFlagService, WatchtowerFeatureFlagService>();

        // Stack backups (ADR-0016) — the archive service streams volumes through never-started
        // helper containers, the factory resolves the storage backend per run (runtime-switchable),
        // the queue serializes runs process-wide, and the scheduler opens the daily window. The
        // queue is a singleton so backups.run can enqueue and read coalesced state; hosted for the
        // worker loop and graceful shutdown.
        services.AddSingleton<BackupArchiveService>();
        // Database-aware dumps (ADR-0017): stateless over the engine's exec API, so a singleton.
        services.AddSingleton<PostgresDumpService>();
        services.AddSingleton<BackupStorageFactory>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<BackupQueueService>();
        services.AddHostedService(sp => sp.GetRequiredService<BackupQueueService>());
        services.AddHostedService<BackupBackgroundService>();

        // Background checkers — always registered. Each loops on a short poll and reads its
        // enabled/interval toggle live from IOptionsMonitor<WatchtowerOptions> (backed by the
        // settings-configuration provider), so the toggles are runtime-editable without a restart.
        services.AddHostedService<SelfUpdateBackgroundService>();
        services.AddHostedService<StackUpdateBackgroundService>();
        services.AddHostedService<ImagePruneBackgroundService>();
        // Pull-based deployment — per-stack opt-in (AutoDeployMode), so no global toggle: the
        // minute tick is a single cheap SQLite query when nothing is configured.
        services.AddHostedService<AutoDeployBackgroundService>();

        return services;
    }
}
