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

        // Scoped data-access helpers (wrap the scoped DbContext).
        services.AddScoped<RegistryAuthBuilder>();

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

        // Reverse proxy (Caddy) — singleton for handler/deploy triggers; hosted so the proxy topology
        // (networks + container + routes) is reconciled on startup. No-op unless Proxy:Enabled.
        services.AddSingleton<CaddyManager>();
        services.AddHostedService(sp => sp.GetRequiredService<CaddyManager>());

        services.AddSingleton<StackUpdateService>();

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
        // Only the data-protector provider: password resets (including the break-glass hook) go
        // through a token so the validators run before the stored hash is touched. The phone/email/
        // authenticator providers AddDefaultTokenProviders would bring have nothing to drive them.
        .AddTokenProvider<DataProtectorTokenProvider<User>>(TokenOptions.DefaultProvider);

        // Login sessions (design.md §4): revocable database rows behind the __wt_sso cookie. Scoped, like
        // the context it writes through. Registered unconditionally — it is inert until something logs in.
        services.AddScoped<AuthSessionService>();

        // Who the caller is, and therefore what [assembly: ElarionAuthorizationDefaults] lets through.
        // Registered BEFORE AddElarionClaimsCurrentUser on purpose: that helper uses TryAdd for
        // ICurrentUser, so registering first is how a host substitutes its own snapshot.
        var authEnabled = section.GetValue<bool>("Auth:Enabled");
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

        // First-run admin + break-glass password reset. No-op unless Auth:Enabled.
        services.AddHostedService<AuthBootstrapService>();

        // CI runners (docs/ci-runners/design.md) — the orchestrator reconciles ephemeral GitHub
        // Actions runner containers for enabled repos; singleton so ci.* handlers can read live
        // status and wake it after config changes. Idle cost with no repos configured: one SQLite
        // query + one Docker label query per pass.
        services.AddSingleton<GitHubApiClient>();
        services.AddSingleton<CiRunnerOrchestrator>();
        services.AddHostedService(sp => sp.GetRequiredService<CiRunnerOrchestrator>());

        // Metrics backend (ADR-0007) — pluggable and mutually exclusive, so exactly one collector runs.
        // Default ("memory"): the in-memory ring buffer fed by the background sampler (amendment F5),
        // zero external dependency; the RPC handlers read only the store, no Docker fan-out on the path.
        // Opt-in ("influxdb"): read host + container series (incl. history) from an InfluxDB an external
        // collector populates — the sampler/store are NOT registered, so Watchtower runs no collector of
        // its own and InfluxDB is the single source of truth. Switching backends requires a restart.
        var metricsBackend = section.GetValue<string>("Metrics:Backend");
        if (string.Equals(metricsBackend, "influxdb", StringComparison.OrdinalIgnoreCase)) {
            services.AddSingleton<IMetricsSource, InfluxMetricsSource>();
        } else {
            services.AddSingleton<MetricsStore>();
            services.AddHostedService<MetricsSampler>();
            services.AddSingleton<IMetricsSource, InMemoryMetricsSource>();
        }

        // Client-exposed feature flags (ADR-0030): the session bootstrap evaluates the Metrics module's
        // [ClientFeatures] names through this service — "metrics-history" reflects the backend chosen above.
        services.AddSingleton<IFeatureFlagService, MetricsFeatureFlagService>();

        // Background checkers — always registered. Each loops on a short poll and reads its
        // enabled/interval toggle live from IOptionsMonitor<WatchtowerOptions> (backed by the
        // settings-configuration provider), so the toggles are runtime-editable without a restart.
        services.AddHostedService<SelfUpdateBackgroundService>();
        services.AddHostedService<StackUpdateBackgroundService>();
        // Pull-based deployment — per-stack opt-in (AutoDeployMode), so no global toggle: the
        // minute tick is a single cheap SQLite query when nothing is configured.
        services.AddHostedService<AutoDeployBackgroundService>();

        return services;
    }
}
