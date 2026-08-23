using Elarion.Abstractions.Identity;
using Elarion.Settings;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Metrics.Handlers;

/// <summary>
/// Switches the metrics backend at runtime (ADR-0013). Persists <c>Watchtower:Metrics:*</c> as
/// Global-scope settings, which layer over the appsettings defaults via the settings-backed
/// configuration provider and re-bind into <see cref="WatchtowerOptions"/> — the router serves the new
/// backend on the next read and the sampler follows on its next tick, no restart. A null
/// <see cref="Command.InfluxToken"/> keeps the stored token, so the UI never has to echo the secret.
/// Paths pinned by <c>WATCHTOWER__*</c> env vars (which win over the store) are rejected when the
/// request tries to change them, and never written.
/// </summary>
[Handler("metrics.updateConfig")]
public sealed class UpdateMetricsConfig(
    ISettingsManager settings,
    IOptionsMonitor<WatchtowerOptions> options,
    EnvironmentSettingPins pins,
    AuditLog audit,
    ICurrentUser currentUser)
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
        // The pre-ADR-0024 spelling of "database" is accepted and stored under its new name, so an
        // operator retyping what their old compose file said does not get a validation error.
        if (backend == MetricsOptions.LegacyDatabaseBackendName) backend = "database";
        if (backend is not ("memory" or "database" or "influxdb"))
            return AppError.Validation("Backend must be one of: memory, database, influxdb.");
        if (command.RetentionDays is < 1 or > 365)
            return AppError.Validation("RetentionDays must be between 1 and 365.");

        // Effective connection values after this update: supplied value, else what is already configured.
        var effective = options.CurrentValue.Metrics;
        var current = effective.Influx;
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

        // Reject changes to env-pinned paths (env wins — a stored row would silently not take effect).
        // An omitted (null) influx field is "keep what's stored" and never conflicts with a pin; a
        // supplied token counts as a change outright, because the stored secret can't be compared.
        var violations = new List<string>();
        void Check(string path, bool changed) {
            if (changed && pins.IsPinned(path)) violations.Add(path);
        }
        Check(WatchtowerSettingPaths.MetricsBackend, backend != effective.ResolveBackend().ToConfigValue());
        Check(WatchtowerSettingPaths.MetricsRetentionDays, command.RetentionDays != effective.RetentionDays);
        Check(WatchtowerSettingPaths.MetricsInfluxUrl, Changed(command.InfluxUrl, current.Url));
        Check(WatchtowerSettingPaths.MetricsInfluxOrg, Changed(command.InfluxOrg, current.Org));
        Check(WatchtowerSettingPaths.MetricsInfluxBucket, Changed(command.InfluxBucket, current.Bucket));
        Check(WatchtowerSettingPaths.MetricsInfluxToken, command.InfluxToken is not null);
        Check(WatchtowerSettingPaths.MetricsInfluxComposeProjectTag, Changed(command.InfluxComposeProjectTag, current.ComposeProjectTag));
        Check(WatchtowerSettingPaths.MetricsInfluxDiskMountpoint, Changed(command.InfluxDiskMountpoint, current.DiskMountpoint));
        if (violations.Count > 0)
            return EnvironmentSettingPins.PinnedError(violations);

        await SetUnlessPinnedAsync(WatchtowerSettingPaths.MetricsBackend, backend, ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.MetricsRetentionDays, command.RetentionDays.ToString(), ct);
        if (command.InfluxUrl is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.MetricsInfluxUrl, command.InfluxUrl.Trim(), ct);
        if (command.InfluxOrg is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.MetricsInfluxOrg, command.InfluxOrg.Trim(), ct);
        if (command.InfluxBucket is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.MetricsInfluxBucket, command.InfluxBucket.Trim(), ct);
        if (command.InfluxToken is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.MetricsInfluxToken, command.InfluxToken.Trim(), ct);
        if (command.InfluxComposeProjectTag is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.MetricsInfluxComposeProjectTag, command.InfluxComposeProjectTag.Trim(), ct);
        if (command.InfluxDiskMountpoint is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.MetricsInfluxDiskMountpoint, command.InfluxDiskMountpoint.Trim(), ct);

        // Recorded post-write with the new effective values — secrets appear only as "updated".
        await audit.RecordAsync("metrics", "config.update", "metrics settings",
            $"backend {backend} · retention {command.RetentionDays}d"
            + (backend == "influxdb" ? $" · {url} org {org} bucket {bucket}" : "")
            + (command.InfluxToken is not null ? " · secrets updated: InfluxDB token" : ""),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        // Echo the written values (the config provider reloads asynchronously — same reasoning as
        // system.updateAutomation): immediately consistent for the caller.
        var config = new MetricsConfig(
            Backend: backend,
            RetentionDays: command.RetentionDays,
            HistoryAvailable: backend is "database" or "influxdb",
            Influx: new MetricsInfluxConfig(
                Url: url,
                Org: org,
                Bucket: bucket,
                HasToken: !string.IsNullOrWhiteSpace(token),
                ComposeProjectTag: command.InfluxComposeProjectTag?.Trim() ?? current.ComposeProjectTag,
                DiskMountpoint: command.InfluxDiskMountpoint?.Trim() ?? current.DiskMountpoint),
            PinnedPaths: pins.Pinned(GetMetricsConfig.MetricsPaths));
        return new Response(config);
    }

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
