using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchtower.Application.Config;
using Watchtower.Application.Modules.Backups.Handlers;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups;

/// <summary>
/// Stack backups (ADR-0016): global backup settings (schedule, storage provider, encryption,
/// retention), per-stack enablement, run-now, and the backup history.
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
    string Time,
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
        Time: backup.Time,
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
        PinnedPaths: pins.Pinned(Handlers.GetBackupConfig.BackupPaths));
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

/// <summary>A stack's backup participation: schedule opt-in and the stop-for-snapshot flag.</summary>
public sealed record BackupStackConfigDto(int StackId, bool Enabled, bool StopContainers);

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
public sealed partial class BackupsJsonContext : JsonSerializerContext;
