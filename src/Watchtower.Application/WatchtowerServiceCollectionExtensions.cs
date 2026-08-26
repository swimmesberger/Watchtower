using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;
using Elarion.Authorization;
using Elarion.Coordination.PostgreSql;
using Elarion.Identity;
using Elarion.Scheduling.EntityFrameworkCore;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Elarion.Settings.PostgreSql;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application;

/// <summary>
/// Registers Watchtower's application-layer infrastructure: strongly-typed options, the PostgreSQL
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

        // PostgreSQL is the only database (ADR-0024). One NpgsqlDataSource per process, shared by the
        // EF context and — from the state-in-the-database phase on — by Elarion's PostgreSQL packages
        // (role leases, LISTEN/NOTIFY settings propagation), which is why it is a registered singleton
        // rather than something UseNpgsql(connectionString) builds privately per context.
        var connectionString = WatchtowerConnectionString.Resolve(config);
        services.TryAddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());

        services.AddDbContext<WatchtowerDbContext>((sp, o) =>
            o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>())
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
        // Find-or-create over the product catalogue (ADR-0026), shared by stacks.create and
        // templates.create so both keep their inline repository fields and resolve to one product.
        services.AddScoped<ProductCatalog>();
        // Release intake (ADR-0026 decision 3), shared by the product release webhook and
        // products.createRelease so both validate, resolve and fingerprint identically. The digest
        // resolver is the one part that leaves the machine and is registered separately — a test
        // substitutes it, which is what keeps intake testable without a registry.
        services.AddScoped<ReleaseIntakeService>();
        services.AddSingleton<IReleaseDigestResolver, RegistryDigestResolver>();
        // Release retention (design.md §"Release retention"): the post-create pruning pass intake runs.
        // Scoped, and its own service rather than a private method on intake, so its four protection
        // rules can be driven — and mutation-tested — one at a time.
        services.AddScoped<ReleasePruner>();
        // Release fan-out (design.md §Convergent fan-out): reads the target predicate through the scoped
        // context and enqueues onto the singleton deploy queue. Shared by the release webhook and
        // products.deployRelease so "which stacks does a release reach" has one answer.
        services.AddScoped<ReleaseRolloutService>();
        // The pin/rollback pre-flight (design.md §"Image pinning"): every image of the target release is
        // HEADed before anything is written, so a garbage-collected digest is a refusal rather than a
        // mid-rollback surprise.
        services.AddScoped<ReleaseImageValidator>();
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
        // …and its cross-instance change channel (ADR-0024 decision 6). This is what turns a settings
        // write on one node into an IChangeToken firing on every node — including the internal
        // Watchtower:Proxy:RoutesVersion key that carries route, realm and certificate changes to the
        // other instances' route tables and SNI maps. The data source is the one registered above, so
        // the listener shares the process's connection configuration rather than parsing its own.
        // The connection-string overload rather than the shared NpgsqlDataSource above, deliberately:
        // a LISTEN connection is held open for the life of the process, so borrowing one from the pool
        // the request path uses would permanently shrink it. The package owns and disposes the small
        // data source it builds for that one connection.
        services.AddElarionPostgreSqlSettingsChanges(connectionString);

        // Leader election for the roles exactly one instance may play. Today that is certificate
        // ordering: every instance serves from proxy_certificates, one holds `acme-issuer` and orders
        // (ADR-0024 decision 5). The same primitive carries the `control` role in the next ADR.
        services.AddElarionPostgreSqlRoleLease<WatchtowerDbContext>(
            o => o.RoleName = CertificateManager.IssuerRole);

        // Scheduler occurrence claims, so a [ScheduledJob] — today the backup schedule's minute tick
        // (ADR-0018) — fires once cluster-wide instead of once per instance. Without this, two instances
        // would each start the nightly backup of every stack.
        services.AddElarionSchedulerEntityFrameworkCore<WatchtowerDbContext>();

        // Deploy queue — singleton for enqueuing; hosted for graceful shutdown.
        services.AddSingleton<DeployQueueService>();
        services.AddHostedService(sp => sp.GetRequiredService<DeployQueueService>());

        // Self-update — singleton + hosted so an in-progress apply is reconciled on startup and
        // cancelled cleanly on shutdown.
        services.AddSingleton<SelfUpdateService>();
        services.AddHostedService(sp => sp.GetRequiredService<SelfUpdateService>());

        // Reverse proxy — ADR-0015, extended by ADR-0022 for the third provider, which is also the
        // default. Three of them behind one runtime router, mirroring the metrics backend (ADR-0007):
        // the in-process proxy (Watchtower binds 80/443 itself, no sibling container), Caddy (a sibling
        // container on host ports 80/443, automatic TLS — deprecated) and
        // Cloudflare Tunnel (cloudflared + the Cloudflare API). All three are registered unconditionally and
        // hosted so the active one reconciles on startup (each self-gates on Proxy:Enabled +
        // Proxy:Provider); consumers inject IProxyProvider and the router resolves the selected backend
        // per call, which is what makes the provider switchable from the Settings page without a restart.
        // The general audit trail (audit.listEvents). Singleton — its writers are the singleton
        // providers; TryAdd so tests can substitute a recording double.
        services.TryAddSingleton<AuditLog>();
        // Encrypts the private keys that now live in the database — certificates, the ACME account, the
        // identity-assertion signing key (ADR-0024). Inert (and says so once) until
        // Watchtower:Auth:KeyProtectionSecret is set.
        services.TryAddSingleton<KeyProtector>();
        services.AddSingleton<ProxyIngressNetworks>();
        services.AddSingleton<CaddyManager>();
        services.AddHostedService(sp => sp.GetRequiredService<CaddyManager>());
        services.AddSingleton<CloudflareApiClient>();
        services.AddSingleton<CloudflareTunnelProvider>();
        services.AddHostedService(sp => sp.GetRequiredService<CloudflareTunnelProvider>());
        // In-process provider: the routing table and the listener outcome are process state the request
        // path reads, so both are singletons independent of whether the provider is the active one.
        services.AddSingleton<ProxyRouteTable>();
        services.AddSingleton<YarpListenerState>();
        // Diagnostics for the in-process proxy's listeners (ADR-0022 addendum): a best-effort read of what
        // the server actually bound, which is what lets the status surface notice a rebind that failed.
        // (The projection's warning sink is registered by the host instead — it has to exist before the
        // container does, because the projection is built before Build().)
        services.AddSingleton<BoundListenerPorts>();
        services.AddSingleton<RouteStatusUpdater>();
        // The live HTTP-01 challenge answers — rows since ADR-0024, so the CA's validation request can
        // land on any instance. Registered unconditionally, like the table above: the middleware that
        // reads it is in the pipeline whatever the provider is, and an empty table simply answers 404.
        services.AddSingleton<AcmeHttpChallengeStore>();
        // The ACME account key, one row per directory URL.
        services.AddSingleton<AcmeAccountStore>();
        // How a route, realm or certificate write on this instance reaches the others (ADR-0024
        // decision 6). A singleton because the watchers it hands out are process-lifetime.
        services.AddSingleton<ProxyChangeSignal>();
        // A/AAAA resolution, shared by proxy.checkDns and the issuer's preflight so the operator's
        // "check DNS" button and the certificate machinery cannot come to different answers.
        services.AddSingleton<DnsPreflight>();
        // Certificate issuance (ADR-0022): the protocol half, and the background loop that schedules it.
        // TryAdd on the transport so a test can substitute an in-process CA's message handler.
        services.TryAddSingleton<IAcmeTransportFactory, AcmeTransportFactory>();
        services.AddSingleton<CertificateIssuer>();
        services.AddSingleton<CertificateManager>();
        services.AddSingleton<IProxyCertificateManager>(sp => sp.GetRequiredService<CertificateManager>());
        services.AddHostedService(sp => sp.GetRequiredService<CertificateManager>());
        services.AddSingleton<YarpProxyProvider>();
        services.AddHostedService(sp => sp.GetRequiredService<YarpProxyProvider>());
        services.AddSingleton<IProxyProvider, ProxyProviderRouter>();
        // The one-time "an existing Caddy install keeps Caddy" upgrade step (ADR-0022). Scoped because it
        // reads the routes table; run once from Program.InitializeDatabaseAsync, before the providers start.
        services.AddScoped<ProxyProviderMigration>();
        // The other one-time upgrade step: a configured Auth:Host becomes the system realm's Watchtower
        // route (ADR-0023). Scoped and run from the same place, and after the migration — which is what
        // converts the realms' own stored auth hosts.
        services.AddScoped<LoginHostConversion>();
        // ADR-0024's: the key and certificate files a pre-PostgreSQL installation left on /data become
        // rows, once. Scoped like the two above and run from the same place, ahead of both consumers —
        // see WatchtowerStateInitializer for why the order matters.
        services.AddScoped<FileStateImport>();

        // The SNI map, cached over the proxy_certificates table (ADR-0024). Registered unconditionally
        // like the rest of the proxy services; filled by WatchtowerStateInitializer before Kestrel
        // serves, because the handshake path cannot wait for a query.
        services.AddSingleton<CertificateStore>();

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
        // tokens and the OIDC correlation/nonce cookies. The key ring is persisted to the database
        // (ADR-0024) — unconditionally, because the default location is a per-user directory and the
        // shipped container has no home directory, which would make the keys ephemeral and sign
        // everyone out on every restart. Rows rather than files because a token or cookie minted on one
        // instance has to be readable on every other, which is the whole reason the ring moved.
        services.AddDataProtection().PersistKeysToDbContext<WatchtowerDbContext>();
        // …and encrypted at rest with the same secret as every other private key, when one is
        // configured. Registered through the options builder rather than inline because the decision
        // needs the KeyProtector, which needs the bound options: an unset secret leaves ASP.NET's
        // default (plaintext elements), which is what keeps a ring written before the secret was
        // configured readable — those rows carry no encryptedKey wrapper for a decryptor to look up.
        // Only newly generated keys are encrypted; existing ones are not rewritten, because the key
        // manager treats the ring as append-only and rewriting it is how a ring loses keys.
        services.AddOptions<KeyManagementOptions>().Configure<KeyProtector>((keyManagement, protector) => {
            if (!protector.IsEncrypting) return;
            keyManagement.XmlEncryptor = new KeyProtectorXmlEncryptor(protector);
        });
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

        // The forward-auth decision (design.md §5): may this request enter that app, and as whom. Scoped
        // like the context it reads through, and registered unconditionally alongside the other auth
        // services: nothing resolves it while Auth:Enabled is false — the verify endpoint is mapped as a
        // bare 404 in that mode. Shared with the in-process proxy so the two transports cannot come to
        // different verdicts — ADR-0022.
        services.AddScoped<AccessVerifier>();

        // Two-factor (TOTP + recovery codes, design.md §4). Scoped, like the UserManager and the context it
        // writes through. Registered unconditionally and inert until an account enrols.
        services.AddScoped<UserMfaService>();

        // The ES256 signer behind X-Watchtower-Jwt and the JWKS endpoint (design.md §2.3). Singleton
        // because the key pair is process-wide state: loading it per request would query on every
        // proxied request, and generating it per request would produce a different `kid` each time. The
        // key is a row since ADR-0024, read once by WatchtowerStateInitializer before anything is
        // served, so every instance mints under the `kid` the JWKS advertises.
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
        // status and wake it after config changes. Idle cost with no repos configured: one database
        // query + one Docker label query per pass.
        services.AddSingleton<GitHubApiClient>();
        // The per-repo GitHub Actions config pass the orchestrator runs beside the runner reconcile:
        // registry credentials (docs/ci-runners/design.md) and release configuration
        // (docs/products/design.md §"Secret sync"), independently hashed and independently isolated.
        services.AddSingleton<CiActionsConfigSync>();
        services.AddSingleton<CiRunnerOrchestrator>();
        services.AddHostedService(sp => sp.GetRequiredService<CiRunnerOrchestrator>());
        // Product → CI repo link (ADR-0026 decision 7): reads go through the FK, and the resolver
        // records it the first time it can derive one from the repository URL.
        services.AddScoped<CiRepoResolver>();
        // Toolchain detection piggybacks on deploy clones; the recorder persists the profile and
        // wakes the orchestrator so the toolcache warmer converges (docs/ci-runners/design.md).
        services.AddSingleton<CiToolchainRecorder>();

        // Metrics backend (ADR-0007, amended by ADR-0013) — three backends behind one runtime router:
        // "database" (default) persists windowed history next to the live ring, "memory" keeps the ring
        // only, "influxdb" reads an externally-collected InfluxDB. Everything is registered
        // unconditionally; the router resolves the backend from IOptionsMonitor per call and the sampler
        // re-checks it per tick (idling under influxdb so exactly one collector runs). That is what
        // makes the backend switchable from the Settings page without a restart.
        services.AddSingleton<MetricsStore>();
        services.AddSingleton<MetricsPersistenceService>();
        services.AddHostedService<MetricsSampler>();
        services.AddSingleton<InMemoryMetricsSource>();
        services.AddSingleton<DatabaseMetricsSource>();
        services.AddSingleton<IMetricsSource, MetricsSourceRouter>();

        // Client-exposed feature flags (ADR-0030): the session bootstrap evaluates every module's
        // [ClientFeatures] names through this one service, per call — "metrics-history" follows the
        // routed metrics backend above (including across a runtime switch), "apps-portal" reflects the
        // caller's realm. Scoped because the second of those reads ICurrentUser; a singleton could only
        // answer the deployment-scoped half.
        services.AddScoped<IFeatureFlagService, WatchtowerFeatureFlagService>();

        // Stack backups (ADR-0016) — the archive service streams volumes through never-started
        // helper containers, the factory resolves the storage backend per run (runtime-switchable),
        // and the queue serializes runs process-wide. The queue is a singleton so backups.run can
        // enqueue and read coalesced state; hosted for the worker loop and graceful shutdown. The
        // schedule itself is an Elarion [ScheduledJob] minute tick (BackupScheduleJob, ADR-0018),
        // registered by AddElarion with the Backups module and run by the host's AddElarionScheduler.
        services.AddSingleton<BackupArchiveService>();
        // Database-aware dumps (ADR-0017): stateless over the engine's exec API, so a singleton.
        services.AddSingleton<PostgresDumpService>();
        services.AddSingleton<BackupStorageFactory>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<BackupQueueService>();
        services.AddHostedService(sp => sp.GetRequiredService<BackupQueueService>());

        // Background checkers — always registered. Each loops on a short poll and reads its
        // enabled/interval toggle live from IOptionsMonitor<WatchtowerOptions> (backed by the
        // settings-configuration provider), so the toggles are runtime-editable without a restart.
        services.AddHostedService<SelfUpdateBackgroundService>();
        services.AddHostedService<StackUpdateBackgroundService>();
        services.AddHostedService<ImagePruneBackgroundService>();
        // Pull-based deployment — per-stack opt-in (AutoDeployMode), so no global toggle: the
        // minute tick is a single cheap database query when nothing is configured.
        services.AddHostedService<AutoDeployBackgroundService>();
        // Stack desired state (ADR-0025): one startup pass that re-stops deliberately stopped
        // stacks whose containers a Docker restart policy revived, retrying while the daemon
        // comes up, then exits.
        services.AddHostedService<StackDesiredStateReconciler>();

        return services;
    }
}
