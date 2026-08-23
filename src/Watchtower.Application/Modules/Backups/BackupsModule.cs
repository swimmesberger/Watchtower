using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Backups.Handlers;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups;

/// <summary>
/// Stack backups (ADR-0016): global backup settings (schedule, storage provider, encryption,
/// retention), per-stack enablement and schedule override (ADR-0018), run-now, and the backup history.
/// </summary>
[AppModule("Backups")]
public static partial class BackupsModule {
    /// <summary>Returns the JSON type info resolver for Backups module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => BackupsJsonContext.Default;
}

/// <summary>
/// The backup configuration surfaced to the Settings page. Secrets (encryption passphrase, SFTP
/// password/private key) are reduced to has-a-value flags; env-pinned paths ride along so the UI can
/// disable those fields (ADR-0014).
/// </summary>
public sealed record BackupConfigDto(
    bool Enabled,
    string Cron,
    string? InstanceName,
    string ResolvedInstanceName,
    int RetentionDays,
    int RetentionMaxCount,
    bool HasEncryptionPassphrase,
    string HelperImage,
    string Provider,
    BackupSftpConfigDto Sftp,
    string LocalBasePath,
    string[] PinnedPaths) {
    internal static BackupConfigDto From(BackupOptions backup, EnvironmentSettingPins pins) => new(
        Enabled: backup.Enabled,
        Cron: BackupSchedule.ResolveGlobalExpression(backup),
        InstanceName: backup.InstanceName,
        ResolvedInstanceName: backup.ResolveInstanceName(),
        RetentionDays: backup.RetentionDays,
        RetentionMaxCount: backup.RetentionMaxCount,
        HasEncryptionPassphrase: !string.IsNullOrEmpty(backup.EncryptionPassphrase),
        HelperImage: backup.HelperImage,
        Provider: backup.ResolveProvider() == BackupProviderKind.Local ? "local" : "sftp",
        Sftp: new BackupSftpConfigDto(
            Host: backup.Sftp.Host,
            Port: backup.Sftp.Port,
            Username: backup.Sftp.Username,
            HasPassword: !string.IsNullOrEmpty(backup.Sftp.Password),
            HasPrivateKey: !string.IsNullOrEmpty(backup.Sftp.PrivateKey),
            BasePath: backup.Sftp.BasePath),
        LocalBasePath: backup.Local.BasePath,
        PinnedPaths: ResolvePinnedPaths(pins));

    /// <summary>
    /// The pinned paths, with the legacy <c>Backup:Time</c> env var reported as pinning the schedule
    /// too: it is what the effective expression comes from while it is set, so the UI's cron field has
    /// to lock exactly as if <c>Backup:Cron</c> were pinned.
    /// </summary>
    internal static string[] ResolvePinnedPaths(EnvironmentSettingPins pins) {
        var pinned = pins.Pinned(Handlers.GetBackupConfig.BackupPaths);
        if (pins.IsPinned(WatchtowerSettingPaths.BackupTime) && !pins.IsPinned(WatchtowerSettingPaths.BackupCron))
            pinned = [.. pinned, WatchtowerSettingPaths.BackupCron];
        return pinned;
    }
}

/// <summary>SFTP connection values for the config surface (secrets reduced to flags).</summary>
public sealed record BackupSftpConfigDto(
    string? Host,
    int Port,
    string? Username,
    bool HasPassword,
    bool HasPrivateKey,
    string BasePath);

/// <summary>One backup run for the history views (per stack and instance-wide).</summary>
public sealed record BackupEventDto(
    int Id,
    int StackId,
    string StackName,
    string TriggeredBy,
    string Status,
    string? RemotePath,
    long? SizeBytes,
    string? Output,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt);

/// <summary>
/// A stack's backup participation: schedule opt-in, the stop-for-snapshot flag, how its stateful
/// containers are quiesced (<c>stop</c> or <c>pause</c>, ADR-0019) and its schedule override (a
/// five-field cron expression; null = the instance-wide schedule applies).
/// </summary>
public sealed record BackupStackConfigDto(
    int StackId, bool Enabled, bool StopContainers, string? Cron, string QuiesceMode) {
    internal static BackupStackConfigDto From(Entities.Stack stack) => new(
        stack.Id, stack.BackupEnabled, stack.BackupStopContainers, stack.BackupCron,
        BackupQuiesceModes.ToWire(stack.BackupQuiesceMode));
}

/// <summary>The wire form of <see cref="BackupQuiesceMode"/>: lowercase, like every other enum on this API.</summary>
internal static class BackupQuiesceModes {
    public const string Stop = "stop";
    public const string Pause = "pause";

    public static string ToWire(BackupQuiesceMode mode) => mode == BackupQuiesceMode.Pause ? Pause : Stop;

    /// <summary>Null and blank read as <see cref="Stop"/> (the default); anything else has to be one of the two words.</summary>
    public static bool TryParse(string? value, out BackupQuiesceMode mode) {
        mode = BackupQuiesceMode.Stop;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (string.Equals(value.Trim(), Stop, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(value.Trim(), Pause, StringComparison.OrdinalIgnoreCase)) return false;
        mode = BackupQuiesceMode.Pause;
        return true;
    }
}

/// <summary>
/// Per-service backup settings configured in the UI (ADR-0020), in the compose labels' own value
/// syntax: <c>exclude</c> stands in for <c>watchtower.backup.exclude=true</c>, <c>stop</c> for
/// <c>watchtower.backup.stop</c> (<c>true</c>/<c>false</c>/<c>pause</c>), <c>dump</c> for
/// <c>watchtower.backup.dump</c> (<c>false</c>/<c>postgres</c>). Null = not set.
/// </summary>
public sealed record BackupServiceOverrideDto(string Service, bool Exclude, string? Stop, string? Dump) {
    internal static BackupServiceOverrideDto From(string service, BackupServiceOverride o) =>
        new(service, o.Exclude, o.Stop, o.Dump);
}

/// <summary>One row of the plan preview: a container, what the next run would do with it, why, and the inputs.</summary>
/// <param name="Service">The compose service (the container name for a container without one).</param>
/// <param name="Container">The container's name; null for an override whose service is not deployed.</param>
/// <param name="State"><c>running</c>, <c>not running</c> or <c>absent</c>.</param>
/// <param name="Volumes">Named volumes the container mounts.</param>
/// <param name="Action"><c>stop</c>, <c>pause</c>, <c>keep</c>, <c>dump</c>, <c>excluded</c> or <c>notRunning</c>.</param>
/// <param name="Reason">Operator-facing prose.</param>
/// <param name="Source"><c>default</c>, <c>label</c> or <c>override</c>.</param>
/// <param name="ExcludeLabel">The raw compose label, or null.</param>
/// <param name="StopLabel">The raw compose label, or null.</param>
/// <param name="DumpLabel">The raw compose label, or null.</param>
/// <param name="Override">The UI override for the service, or null.</param>
public sealed record BackupServicePreviewDto(
    string Service,
    string? Container,
    string State,
    IReadOnlyList<string> Volumes,
    string Action,
    string Reason,
    string Source,
    string? ExcludeLabel,
    string? StopLabel,
    string? DumpLabel,
    BackupServiceOverrideDto? Override) {
    internal static BackupServicePreviewDto From(BackupServicePreview row) => new(
        row.Service, row.Container, row.State, row.Volumes,
        row.Action switch {
            BackupServiceAction.Stop => "stop",
            BackupServiceAction.Pause => "pause",
            BackupServiceAction.Keep => "keep",
            BackupServiceAction.Dump => "dump",
            BackupServiceAction.Excluded => "excluded",
            _ => "notRunning",
        },
        row.Reason,
        BackupSettingSources.ToWire(row.Source),
        row.ExcludeLabel, row.StopLabel, row.DumpLabel,
        row.Override is { } o ? BackupServiceOverrideDto.From(row.Service, o) : null);
}

/// <summary>A candidate volume the run would leave out, with why.</summary>
public sealed record BackupExcludedVolumeDto(string Name, string Reason, string Detail);

/// <summary>
/// The dry run the Backups tab shows: what the next run would archive, quiesce, dump and skip for the
/// stack as deployed right now (ADR-0020). <see cref="LabelSnippet"/> renders the UI overrides as
/// compose labels to paste.
/// </summary>
public sealed record BackupPlanPreviewDto(
    bool Deployed,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<BackupExcludedVolumeDto> ExcludedVolumes,
    IReadOnlyList<BackupServicePreviewDto> Services,
    IReadOnlyList<string> Warnings,
    string? LabelSnippet) {
    internal static BackupPlanPreviewDto From(BackupPlanPreview preview) => new(
        preview.Deployed,
        preview.Volumes,
        [.. preview.ExcludedVolumes.Select(v => new BackupExcludedVolumeDto(
            v.Name, v.Reason == BackupVolumeExclusionReason.Label ? "label" : "dump", v.Detail))],
        [.. preview.Services.Select(BackupServicePreviewDto.From)],
        preview.Warnings,
        preview.LabelSnippet);
}

/// <summary>The wire form of <see cref="BackupSettingSource"/>.</summary>
internal static class BackupSettingSources {
    public static string ToWire(BackupSettingSource source) => source switch {
        BackupSettingSource.Label => "label",
        BackupSettingSource.Override => "override",
        _ => "default",
    };
}

/// <summary>One archive present on the storage — the restore picker's row.</summary>
public sealed record BackupRemoteFileDto(string Name, long SizeBytes, DateTimeOffset TakenAt, bool Encrypted);

/// <summary>Returned immediately after a run is enqueued; the event tracks progress.</summary>
public sealed record BackupRunAcceptedDto(int BackupEventId, string Status);

/// <summary>JSON serializer context for Backups module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(BackupConfigDto))]
[JsonSerializable(typeof(BackupSftpConfigDto))]
[JsonSerializable(typeof(BackupEventDto))]
[JsonSerializable(typeof(BackupStackConfigDto))]
[JsonSerializable(typeof(BackupRunAcceptedDto))]
[JsonSerializable(typeof(GetBackupConfig.Query), TypeInfoPropertyName = "GetBackupConfigQuery")]
[JsonSerializable(typeof(GetBackupConfig.Response), TypeInfoPropertyName = "GetBackupConfigResponse")]
[JsonSerializable(typeof(UpdateBackupConfig.Command), TypeInfoPropertyName = "UpdateBackupConfigCommand")]
[JsonSerializable(typeof(UpdateBackupConfig.Response), TypeInfoPropertyName = "UpdateBackupConfigResponse")]
[JsonSerializable(typeof(TestBackupStorage.Command), TypeInfoPropertyName = "TestBackupStorageCommand")]
[JsonSerializable(typeof(TestBackupStorage.Response), TypeInfoPropertyName = "TestBackupStorageResponse")]
[JsonSerializable(typeof(ListBackupEvents.Query), TypeInfoPropertyName = "ListBackupEventsQuery")]
[JsonSerializable(typeof(ListBackupEvents.Response), TypeInfoPropertyName = "ListBackupEventsResponse")]
[JsonSerializable(typeof(RunBackup.Command), TypeInfoPropertyName = "RunBackupCommand")]
[JsonSerializable(typeof(RunBackup.Response), TypeInfoPropertyName = "RunBackupResponse")]
[JsonSerializable(typeof(BackupRemoteFileDto))]
[JsonSerializable(typeof(ListRemoteBackups.Query), TypeInfoPropertyName = "ListRemoteBackupsQuery")]
[JsonSerializable(typeof(ListRemoteBackups.Response), TypeInfoPropertyName = "ListRemoteBackupsResponse")]
[JsonSerializable(typeof(RestoreBackup.Command), TypeInfoPropertyName = "RestoreBackupCommand")]
[JsonSerializable(typeof(RestoreBackup.Response), TypeInfoPropertyName = "RestoreBackupResponse")]
[JsonSerializable(typeof(GetStackBackupConfig.Query), TypeInfoPropertyName = "GetStackBackupConfigQuery")]
[JsonSerializable(typeof(GetStackBackupConfig.Response), TypeInfoPropertyName = "GetStackBackupConfigResponse")]
[JsonSerializable(typeof(SetStackBackupConfig.Command), TypeInfoPropertyName = "SetStackBackupConfigCommand")]
[JsonSerializable(typeof(SetStackBackupConfig.Response), TypeInfoPropertyName = "SetStackBackupConfigResponse")]
[JsonSerializable(typeof(BackupPlanPreviewDto))]
[JsonSerializable(typeof(BackupServicePreviewDto))]
[JsonSerializable(typeof(BackupServiceOverrideDto))]
[JsonSerializable(typeof(BackupExcludedVolumeDto))]
[JsonSerializable(typeof(GetBackupPlanPreview.Query), TypeInfoPropertyName = "GetBackupPlanPreviewQuery")]
[JsonSerializable(typeof(GetBackupPlanPreview.Response), TypeInfoPropertyName = "GetBackupPlanPreviewResponse")]
[JsonSerializable(typeof(SetBackupServiceOverride.Command), TypeInfoPropertyName = "SetBackupServiceOverrideCommand")]
[JsonSerializable(typeof(SetBackupServiceOverride.Response), TypeInfoPropertyName = "SetBackupServiceOverrideResponse")]
public sealed partial class BackupsJsonContext : JsonSerializerContext;
