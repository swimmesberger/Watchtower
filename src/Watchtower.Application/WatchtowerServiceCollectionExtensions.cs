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

        services.AddSingleton<StackUpdateService>();

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

        // Background checkers — always registered. Each loops on a short poll and reads its
        // enabled/interval toggle live from IOptionsMonitor<WatchtowerOptions> (backed by the
        // settings-configuration provider), so the toggles are runtime-editable without a restart.
        services.AddHostedService<SelfUpdateBackgroundService>();
        services.AddHostedService<StackUpdateBackgroundService>();

        return services;
    }
}
