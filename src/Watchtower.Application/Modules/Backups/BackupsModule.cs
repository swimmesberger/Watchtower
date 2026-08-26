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
/// <remarks>
/// <b>The first five members are the effective policy and have not moved</b>, so every existing reader
/// (the management API, scripts) keeps getting the same answer to "is this stack backed up, and how".
/// What stage 7 of ADR-0026 adds is the two halves that were previously indistinguishable: the stack's
/// <em>own</em> values (<c>Own*</c>, null = inherit) and where each effective value actually came from
/// (<c>*Source</c>) — the Backups tab's "Set by: template policy" labels.
/// </remarks>
/// <param name="StackId">The stack.</param>
/// <param name="Enabled">Effective: whether the schedule includes this stack.</param>
/// <param name="StopContainers">Effective: whether the run quiesces the volume writers.</param>
/// <param name="Cron">Effective: the stack's own expression, or null when it follows the instance schedule.</param>
/// <param name="QuiesceMode">Effective: <c>stop</c> or <c>pause</c>.</param>
/// <param name="OwnEnabled">What the stack itself says, or null when it inherits.</param>
/// <param name="OwnStopContainers">What the stack itself says, or null when it inherits.</param>
/// <param name="OwnCron">What the stack itself says, or null when it inherits.</param>
/// <param name="OwnQuiesceMode">What the stack itself says, or null when it inherits.</param>
/// <param name="EnabledSource">One of <c>stack</c>, <c>template</c>, <c>instance</c>.</param>
/// <param name="StopContainersSource">One of <c>stack</c>, <c>template</c>, <c>instance</c>.</param>
/// <param name="CronSource">One of <c>stack</c>, <c>template</c>, <c>instance</c>.</param>
/// <param name="QuiesceModeSource">One of <c>stack</c>, <c>template</c>, <c>instance</c>.</param>
/// <param name="TemplateId">The template whose policy this stack inherits, or null for a standalone stack.</param>
/// <param name="TemplateName">Its name, so the UI can say <em>which</em> fleet policy without a second query.</param>
public sealed record BackupStackConfigDto(
    int StackId,
    bool Enabled,
    bool StopContainers,
    string? Cron,
    string QuiesceMode,
    bool? OwnEnabled,
    bool? OwnStopContainers,
    string? OwnCron,
    string? OwnQuiesceMode,
    string EnabledSource,
    string StopContainersSource,
    string CronSource,
    string QuiesceModeSource,
    int? TemplateId,
    string? TemplateName) {
    internal static BackupStackConfigDto From(Entities.Stack stack) {
        var policy = BackupPolicyResolver.Resolve(stack, stack.Template);
        return new BackupStackConfigDto(
            stack.Id,
            policy.Enabled,
            policy.StopContainers,
            policy.Cron,
            BackupQuiesceModes.ToWire(policy.QuiesceMode),
            stack.BackupEnabled,
            stack.BackupStopContainers,
            stack.BackupCron,
            stack.BackupQuiesceMode is { } own ? BackupQuiesceModes.ToWire(own) : null,
            BackupPolicySources.ToWire(policy.EnabledSource),
            BackupPolicySources.ToWire(policy.StopContainersSource),
            BackupPolicySources.ToWire(policy.CronSource),
            BackupPolicySources.ToWire(policy.QuiesceModeSource),
            stack.TemplateId,
            stack.Template?.Name);
    }
}

/// <summary>
/// A template's backup policy — the rung every tenant inherits. Every field is nullable: null means the
/// template has no opinion and the instance default applies.
/// </summary>
/// <param name="TemplateId">The template.</param>
/// <param name="TemplateName">Its name.</param>
/// <param name="Enabled">Whether tenants are in the backup schedule; null = instance default (off).</param>
/// <param name="StopContainers">Whether a tenant's run quiesces the volume writers; null = instance default (on).</param>
/// <param name="Cron">The fleet's schedule expression; null = the instance schedule.</param>
/// <param name="QuiesceMode"><c>stop</c> or <c>pause</c>; null = instance default (stop).</param>
/// <param name="TenantCount">How many tenants the policy reaches — the UI's "applies to N instances".</param>
/// <param name="OverriddenTenantCount">
/// How many of them override at least one of the four fields, so the card can say that moving the policy
/// will not reach all of them. Deliberately a count and not a list: the roster is the Instances tab's job.
/// </param>
/// <param name="ServiceOverrides">
/// The template's own per-service rows, in service order — the fifth thing the policy card edits, and the
/// only honest source for it. The tenants' plan previews render inherited rows too, but a tenant that has
/// a row of its own for a service <em>replaces</em> the template's whole row for that service, so a
/// preview is a view of one tenant's effective ladder rather than of the fleet's setting.
/// Every entry carries <c>inherited: true</c>, because that is what these are from a tenant's point of view.
/// </param>
public sealed record BackupTemplatePolicyDto(
    int TemplateId,
    string TemplateName,
    bool? Enabled,
    bool? StopContainers,
    string? Cron,
    string? QuiesceMode,
    int TenantCount,
    int OverriddenTenantCount,
    IReadOnlyList<BackupServiceOverrideDto> ServiceOverrides) {
    internal static BackupTemplatePolicyDto From(
        Entities.StackTemplate template,
        int tenantCount,
        int overriddenTenantCount,
        IEnumerable<Entities.TemplateBackupServiceOverride> serviceOverrides) => new(
        template.Id,
        template.Name,
        template.BackupEnabled,
        template.BackupStopContainers,
        template.BackupCron,
        template.BackupQuiesceMode is { } mode ? BackupQuiesceModes.ToWire(mode) : null,
        tenantCount,
        overriddenTenantCount,
        [.. serviceOverrides
            .OrderBy(o => o.Service, StringComparer.Ordinal)
            .Select(o => new BackupServiceOverrideDto(o.Service, o.Exclude, o.Stop, o.Dump, Inherited: true))]);
}

/// <summary>
/// How a product's deployments are doing on backups, for the product Backups tab's rollup line
/// ("19 backed up in the last 24 h · 1 failed · 2 never").
/// </summary>
/// <remarks>
/// <para>
/// <b>The four buckets are a partition of <paramref name="Enrolled"/>, in priority order</b> —
/// <see cref="Never"/>, then <see cref="Failed"/>, then <see cref="BackedUpRecently"/>, then
/// <see cref="Stale"/> — so they sum to it exactly and a reader can add them up. Overlapping buckets
/// were the first shape and they were wrong in the way rollups usually are: a stack that had never been
/// backed up *and* whose last attempt failed appeared in two counts, so "1 failed · 2 never" over three
/// stacks read as four problems.
/// </para>
/// <para>
/// <b>The denominator is enrolment, not existence.</b> A stack nobody put in the schedule is not
/// failing at anything, and counting it as "never backed up" turns a deliberate choice into a red
/// number that never goes away. Those are <see cref="NotEnrolled"/>, reported separately and rendered
/// neutrally.
/// </para>
/// </remarks>
/// <param name="Deployments">Every stack of the product.</param>
/// <param name="Enrolled">How many the resolved policy actually includes in the schedule — the denominator.</param>
/// <param name="NotEnrolled">The rest. Deliberate non-participation, not a failure.</param>
/// <param name="BackedUpRecently">Enrolled, with a successful backup inside the window and no newer failure.</param>
/// <param name="Stale">
/// Enrolled, last backed up successfully <em>outside</em> the window, with no newer failure. Not
/// "failed" — nothing went wrong, the schedule simply has not come round (or is switched off).
/// </param>
/// <param name="Failed">
/// Enrolled, has succeeded at some point, and its newest terminal run failed. Excludes
/// <see cref="Never"/>: a stack that has never been backed up is described by that, and saying it twice
/// would double-count the one thing wrong with it.
/// </param>
/// <param name="Never">Enrolled, with no successful backup at all, ever.</param>
/// <param name="WindowHours">The width of the "recently" window, so the UI need not hard-code 24.</param>
public sealed record BackupProductRollupDto(
    int Deployments,
    int Enrolled,
    int NotEnrolled,
    int BackedUpRecently,
    int Stale,
    int Failed,
    int Never,
    int WindowHours);

/// <summary>The wire form of <see cref="BackupQuiesceMode"/>: lowercase, like every other enum on this API.</summary>
internal static class BackupQuiesceModes {
    public const string Stop = "stop";
    public const string Pause = "pause";

    /// <summary>The word a caller sends to clear a value and go back to inheriting.</summary>
    public const string Inherit = "inherit";

    public static string ToWire(BackupQuiesceMode mode) => mode == BackupQuiesceMode.Pause ? Pause : Stop;

    /// <summary>
    /// Reads the tri-state wire value: null, blank and <see cref="Inherit"/> all mean "no opinion, walk
    /// the ladder"; <c>stop</c> and <c>pause</c> are explicit; anything else is refused.
    /// </summary>
    /// <remarks>
    /// A caller that omitted the field used to get an explicit <c>stop</c>. It now gets "inherit", which
    /// resolves to <c>stop</c> for every standalone stack — the same behaviour — and to the fleet's
    /// choice for a tenant, which is the improvement stage 7 exists for.
    /// </remarks>
    public static bool TryParse(string? value, out BackupQuiesceMode? mode) {
        mode = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        var trimmed = value.Trim();
        if (string.Equals(trimmed, Inherit, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(trimmed, Stop, StringComparison.OrdinalIgnoreCase)) {
            mode = BackupQuiesceMode.Stop;
            return true;
        }
        if (!string.Equals(trimmed, Pause, StringComparison.OrdinalIgnoreCase)) return false;
        mode = BackupQuiesceMode.Pause;
        return true;
    }

    /// <summary>The message a rejected quiesce mode is refused with — one wording, both write paths.</summary>
    public static string ParseError(string? value) =>
        $"Unknown quiesce mode '{value}' — expected \"{Stop}\", \"{Pause}\" or \"{Inherit}\".";
}

/// <summary>The wire form of <see cref="BackupPolicySource"/>.</summary>
internal static class BackupPolicySources {
    public static string ToWire(BackupPolicySource source) => source switch {
        BackupPolicySource.Stack => "stack",
        BackupPolicySource.Template => "template",
        _ => "instance",
    };
}

/// <summary>
/// Per-service backup settings configured in the UI (ADR-0020), in the compose labels' own value
/// syntax: <c>exclude</c> stands in for <c>watchtower.backup.exclude=true</c>, <c>stop</c> for
/// <c>watchtower.backup.stop</c> (<c>true</c>/<c>false</c>/<c>pause</c>), <c>dump</c> for
/// <c>watchtower.backup.dump</c> (<c>false</c>/<c>postgres</c>). Null = not set.
/// </summary>
/// <param name="Service">The compose service.</param>
/// <param name="Exclude">Stands in for <c>watchtower.backup.exclude=true</c>.</param>
/// <param name="Stop">Stands in for <c>watchtower.backup.stop</c>.</param>
/// <param name="Dump">Stands in for <c>watchtower.backup.dump</c>.</param>
/// <param name="Inherited">
/// True when the row came from the stack's template rather than from the stack, so the tab can label it
/// and point the edit at the fleet policy instead of offering a stack override that would silently
/// replace the whole inherited row.
/// </param>
public sealed record BackupServiceOverrideDto(
    string Service, bool Exclude, string? Stop, string? Dump, bool Inherited = false) {
    internal static BackupServiceOverrideDto From(string service, BackupServiceOverride o) =>
        new(service, o.Exclude, o.Stop, o.Dump, o.FromTemplate);
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
        BackupSettingSource.Template => "template",
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
[JsonSerializable(typeof(BackupTemplatePolicyDto))]
[JsonSerializable(typeof(BackupProductRollupDto))]
[JsonSerializable(typeof(GetProductBackups.Query), TypeInfoPropertyName = "GetProductBackupsQuery")]
[JsonSerializable(typeof(GetProductBackups.Response), TypeInfoPropertyName = "GetProductBackupsResponse")]
[JsonSerializable(typeof(SetTemplateBackupPolicy.Command), TypeInfoPropertyName = "SetTemplateBackupPolicyCommand")]
[JsonSerializable(typeof(SetTemplateBackupPolicy.Response), TypeInfoPropertyName = "SetTemplateBackupPolicyResponse")]
[JsonSerializable(typeof(SetTemplateBackupServiceOverride.Command), TypeInfoPropertyName = "SetTemplateBackupServiceOverrideCommand")]
[JsonSerializable(typeof(SetTemplateBackupServiceOverride.Response), TypeInfoPropertyName = "SetTemplateBackupServiceOverrideResponse")]
[JsonSerializable(typeof(BackupAllTenants.Command), TypeInfoPropertyName = "BackupAllTenantsCommand")]
[JsonSerializable(typeof(BackupAllTenants.Response), TypeInfoPropertyName = "BackupAllTenantsResponse")]
public sealed partial class BackupsJsonContext : JsonSerializerContext;
