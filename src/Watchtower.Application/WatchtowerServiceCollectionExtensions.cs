using Elarion.Abstractions.Features;
using Elarion.Settings;
using Elarion.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Config;
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
        // Public App API (/api/app/*): token auth + the read models the host endpoints translate.
        // Scoped because it reads through the scoped DbContext; the deploy queue resolves it from a
        // short-lived scope when it needs to materialize a stack's token.
        services.AddScoped<AppApiService>();

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
