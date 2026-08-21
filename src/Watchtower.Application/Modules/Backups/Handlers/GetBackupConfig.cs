using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Returns the backup configuration for the Settings page (ADR-0016): schedule, retention,
/// encryption and storage provider — with every secret reduced to a has-a-value flag. Everything is
/// runtime-switchable through the settings store; env-pinned paths ride along so the UI disables
/// those fields (ADR-0014).
/// </summary>
[Handler("backups.getConfig")]
public sealed class GetBackupConfig(IOptionsMonitor<WatchtowerOptions> options, EnvironmentSettingPins pins)
    : IHandler<GetBackupConfig.Query, Result<GetBackupConfig.Response>> {
    public sealed record Query;

    public sealed record Response(BackupConfigDto Config);

    /// <summary>Every path the backups card manages — shared with <see cref="UpdateBackupConfig"/>.</summary>
    internal static readonly string[] BackupPaths = [
        WatchtowerSettingPaths.BackupEnabled,
        WatchtowerSettingPaths.BackupTime,
        WatchtowerSettingPaths.BackupInstanceName,
        WatchtowerSettingPaths.BackupRetentionDays,
        WatchtowerSettingPaths.BackupRetentionMaxCount,
        WatchtowerSettingPaths.BackupEncryptionPassphrase,
        WatchtowerSettingPaths.BackupHelperImage,
        WatchtowerSettingPaths.BackupProvider,
        WatchtowerSettingPaths.BackupSftpHost,
        WatchtowerSettingPaths.BackupSftpPort,
        WatchtowerSettingPaths.BackupSftpUsername,
        WatchtowerSettingPaths.BackupSftpPassword,
        WatchtowerSettingPaths.BackupSftpPrivateKey,
        WatchtowerSettingPaths.BackupSftpPrivateKeyPassphrase,
        WatchtowerSettingPaths.BackupSftpBasePath,
        WatchtowerSettingPaths.BackupLocalBasePath,
    ];

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var backup = options.CurrentValue.Backup;
        return ValueTask.FromResult<Result<Response>>(new Response(BackupConfigDto.From(backup, pins)));
    }
}
