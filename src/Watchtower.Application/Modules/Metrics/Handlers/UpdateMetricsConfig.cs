using Elarion.Settings;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Metrics.Handlers;

/// <summary>
/// Switches the metrics backend at runtime (ADR-0013). Persists <c>Watchtower:Metrics:*</c> as
/// Global-scope settings, which layer over the env/appsettings defaults via the settings-backed
/// configuration provider and re-bind into <see cref="WatchtowerOptions"/> — the router serves the new
/// backend on the next read and the sampler follows on its next tick, no restart. A null
/// <see cref="Command.InfluxToken"/> keeps the stored token, so the UI never has to echo the secret.
/// </summary>
[Handler("metrics.updateConfig")]
public sealed class UpdateMetricsConfig(ISettingsManager settings, IOptionsMonitor<WatchtowerOptions> options)
    : IHandler<UpdateMetricsConfig.Command, Result<UpdateMetricsConfig.Response>> {
    public sealed record Command(
        string Backend,
        int RetentionDays,
        string? InfluxUrl = null,
        string? InfluxOrg = null,
        string? InfluxBucket = null,
        string? InfluxToken = null,
        string? InfluxComposeProjectTag = null,
        string? InfluxDiskMountpoint = null);

    public sealed record Response(MetricsConfig Config);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var backend = command.Backend.Trim().ToLowerInvariant();
        if (backend is not ("memory" or "sqlite" or "influxdb"))
            return AppError.Validation("Backend must be one of: memory, sqlite, influxdb.");
        if (command.RetentionDays is < 1 or > 365)
            return AppError.Validation("RetentionDays must be between 1 and 365.");

        // Effective connection values after this update: supplied value, else what is already configured.
        var current = options.CurrentValue.Metrics.Influx;
        var url = Coalesce(command.InfluxUrl, current.Url);
        var org = Coalesce(command.InfluxOrg, current.Org);
        var bucket = Coalesce(command.InfluxBucket, current.Bucket);
        var token = Coalesce(command.InfluxToken, current.Token);
        if (backend == "influxdb") {
            if (string.IsNullOrWhiteSpace(url)) return AppError.Validation("The InfluxDB URL is required for the influxdb backend.");
            if (string.IsNullOrWhiteSpace(org)) return AppError.Validation("The InfluxDB organization is required for the influxdb backend.");
            if (string.IsNullOrWhiteSpace(bucket)) return AppError.Validation("The InfluxDB bucket is required for the influxdb backend.");
            if (string.IsNullOrWhiteSpace(token)) return AppError.Validation("An InfluxDB API token is required for the influxdb backend.");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https"))
                return AppError.Validation("The InfluxDB URL must be an absolute http(s) URL.");
        }

        await settings.SetStringAsync("Watchtower:Metrics:Backend", backend, SettingsScope.Global, expectedVersion: null, ct);
        await settings.SetStringAsync("Watchtower:Metrics:RetentionDays", command.RetentionDays.ToString(), SettingsScope.Global, expectedVersion: null, ct);
        if (command.InfluxUrl is not null)
            await settings.SetStringAsync("Watchtower:Metrics:Influx:Url", command.InfluxUrl.Trim(), SettingsScope.Global, expectedVersion: null, ct);
        if (command.InfluxOrg is not null)
            await settings.SetStringAsync("Watchtower:Metrics:Influx:Org", command.InfluxOrg.Trim(), SettingsScope.Global, expectedVersion: null, ct);
        if (command.InfluxBucket is not null)
            await settings.SetStringAsync("Watchtower:Metrics:Influx:Bucket", command.InfluxBucket.Trim(), SettingsScope.Global, expectedVersion: null, ct);
        if (command.InfluxToken is not null)
            await settings.SetStringAsync("Watchtower:Metrics:Influx:Token", command.InfluxToken.Trim(), SettingsScope.Global, expectedVersion: null, ct);
        if (command.InfluxComposeProjectTag is not null)
            await settings.SetStringAsync("Watchtower:Metrics:Influx:ComposeProjectTag", command.InfluxComposeProjectTag.Trim(), SettingsScope.Global, expectedVersion: null, ct);
        if (command.InfluxDiskMountpoint is not null)
            await settings.SetStringAsync("Watchtower:Metrics:Influx:DiskMountpoint", command.InfluxDiskMountpoint.Trim(), SettingsScope.Global, expectedVersion: null, ct);

        // Echo the written values (the config provider reloads asynchronously — same reasoning as
        // system.updateAutomation): immediately consistent for the caller.
        var config = new MetricsConfig(
            Backend: backend,
            RetentionDays: command.RetentionDays,
            HistoryAvailable: backend is "sqlite" or "influxdb",
            Influx: new MetricsInfluxConfig(
                Url: url,
                Org: org,
                Bucket: bucket,
                HasToken: !string.IsNullOrWhiteSpace(token),
                ComposeProjectTag: command.InfluxComposeProjectTag?.Trim() ?? current.ComposeProjectTag,
                DiskMountpoint: command.InfluxDiskMountpoint?.Trim() ?? current.DiskMountpoint));
        return new Response(config);
    }

    private static string? Coalesce(string? supplied, string? existing) =>
        supplied is null ? existing : supplied.Trim();
}
