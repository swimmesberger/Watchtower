using Elarion.Abstractions.Identity;
using Elarion.Settings;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Persists the backup settings as Global-scope settings under <c>Watchtower:Backup:*</c>. The
/// scheduler and the storage factory read the options monitor per tick/run, so changes take effect
/// without a restart. Null secret fields (encryption passphrase, SFTP password/private key/key
/// passphrase) keep the stored value — the UI never has to echo a secret; an empty string clears it.
/// Env-pinned paths are rejected when the request tries to change them, and never written (ADR-0014).
/// The schedule is a cron expression (ADR-0018); a stored legacy <c>Backup:Time</c> alias is removed
/// when the schedule is changed, so the new expression is what takes effect.
/// </summary>
[Handler("backups.updateConfig")]
public sealed class UpdateBackupConfig(
    ISettingsManager settings,
    IOptionsMonitor<WatchtowerOptions> options,
    EnvironmentSettingPins pins,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<UpdateBackupConfig.Command, Result<UpdateBackupConfig.Response>> {
    public sealed record Command(
        bool Enabled,
        string Cron,
        string? InstanceName,
        int RetentionDays,
        int RetentionMaxCount,
        string HelperImage,
        string Provider,
        string? EncryptionPassphrase = null,
        string? SftpHost = null,
        int? SftpPort = null,
        string? SftpUsername = null,
        string? SftpPassword = null,
        string? SftpPrivateKey = null,
        string? SftpPrivateKeyPassphrase = null,
        string? SftpBasePath = null,
        string? LocalBasePath = null);

    public sealed record Response(BackupConfigDto Config);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var provider = command.Provider.Trim().ToLowerInvariant();
        if (provider is not ("sftp" or "local"))
            return AppError.Validation("Provider must be one of: sftp, local.");
        var cron = command.Cron?.Trim() ?? "";
        if (!BackupSchedule.TryParse(cron, out _, out var cronError))
            return AppError.Validation(cronError);
        if (command.RetentionDays is < 0 or > 3650)
            return AppError.Validation("RetentionDays must be between 0 (keep forever) and 3650.");
        if (command.RetentionMaxCount is < 0 or > 10_000)
            return AppError.Validation("RetentionMaxCount must be between 0 (unlimited) and 10000.");
        var helperImage = command.HelperImage?.Trim() ?? "";
        if (helperImage.Length == 0 || helperImage.Contains(' '))
            return AppError.Validation("HelperImage must be a single image reference (default: busybox:stable).");
        if (command.SftpPort is { } port && port is < 1 or > 65535)
            return AppError.Validation("SftpPort must be between 1 and 65535.");

        var backup = options.CurrentValue.Backup;
        var sftp = backup.Sftp;
        var currentCron = BackupSchedule.ResolveGlobalExpression(backup);
        var cronChanged = !string.Equals(cron, currentCron, StringComparison.Ordinal);

        // Reject changes to env-pinned paths (env wins — a stored row would silently not take effect).
        var violations = new List<string>();
        void Check(string path, bool changed) {
            if (changed && pins.IsPinned(path)) violations.Add(path);
        }
        Check(WatchtowerSettingPaths.BackupEnabled, command.Enabled != backup.Enabled);
        Check(WatchtowerSettingPaths.BackupCron, cronChanged);
        // The legacy HH:mm env var is where the effective schedule comes from while it is set — a stored
        // cron could not override it, so it pins the schedule field just like Backup:Cron would.
        Check(WatchtowerSettingPaths.BackupTime, cronChanged);
        Check(WatchtowerSettingPaths.BackupInstanceName, Changed(command.InstanceName, backup.InstanceName));
        Check(WatchtowerSettingPaths.BackupRetentionDays, command.RetentionDays != backup.RetentionDays);
        Check(WatchtowerSettingPaths.BackupRetentionMaxCount, command.RetentionMaxCount != backup.RetentionMaxCount);
        Check(WatchtowerSettingPaths.BackupEncryptionPassphrase, command.EncryptionPassphrase is not null);
        Check(WatchtowerSettingPaths.BackupHelperImage, !string.Equals(helperImage, backup.HelperImage.Trim(), StringComparison.Ordinal));
        Check(WatchtowerSettingPaths.BackupProvider,
            provider != (backup.ResolveProvider() == BackupProviderKind.Local ? "local" : "sftp"));
        Check(WatchtowerSettingPaths.BackupSftpHost, Changed(command.SftpHost, sftp.Host));
        Check(WatchtowerSettingPaths.BackupSftpPort, command.SftpPort is { } p && p != sftp.Port);
        Check(WatchtowerSettingPaths.BackupSftpUsername, Changed(command.SftpUsername, sftp.Username));
        Check(WatchtowerSettingPaths.BackupSftpPassword, command.SftpPassword is not null);
        Check(WatchtowerSettingPaths.BackupSftpPrivateKey, command.SftpPrivateKey is not null);
        Check(WatchtowerSettingPaths.BackupSftpPrivateKeyPassphrase, command.SftpPrivateKeyPassphrase is not null);
        Check(WatchtowerSettingPaths.BackupSftpBasePath, Changed(command.SftpBasePath, sftp.BasePath));
        Check(WatchtowerSettingPaths.BackupLocalBasePath, Changed(command.LocalBasePath, backup.Local.BasePath));
        if (violations.Count > 0)
            return EnvironmentSettingPins.PinnedError(violations);

        await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupEnabled, command.Enabled ? "true" : "false", ct);
        if (cronChanged) {
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupCron, cron, ct);
            // The stored legacy alias would otherwise keep feeding the old time into the fallback chain —
            // and it cannot express the new schedule anyway. Not pinned here (checked above), so a plain remove.
            await settings.RemoveAsync(WatchtowerSettingPaths.BackupTime, SettingsScope.Global, expectedVersion: null, ct);
        }
        if (command.InstanceName is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupInstanceName, command.InstanceName.Trim(), ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupRetentionDays,
            command.RetentionDays.ToString(), ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupRetentionMaxCount,
            command.RetentionMaxCount.ToString(), ct);
        if (command.EncryptionPassphrase is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupEncryptionPassphrase, command.EncryptionPassphrase, ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupHelperImage, helperImage, ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupProvider, provider, ct);
        if (command.SftpHost is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupSftpHost, command.SftpHost.Trim(), ct);
        if (command.SftpPort is { } newPort)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupSftpPort, newPort.ToString(), ct);
        if (command.SftpUsername is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupSftpUsername, command.SftpUsername.Trim(), ct);
        if (command.SftpPassword is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupSftpPassword, command.SftpPassword, ct);
        if (command.SftpPrivateKey is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupSftpPrivateKey, command.SftpPrivateKey.Trim(), ct);
        if (command.SftpPrivateKeyPassphrase is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupSftpPrivateKeyPassphrase, command.SftpPrivateKeyPassphrase, ct);
        if (command.SftpBasePath is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupSftpBasePath, command.SftpBasePath.Trim(), ct);
        if (command.LocalBasePath is not null)
            await SetUnlessPinnedAsync(WatchtowerSettingPaths.BackupLocalBasePath, command.LocalBasePath.Trim(), ct);

        // Echo the written values (the config provider reloads asynchronously — same reasoning as
        // proxy.updateConfig): immediately consistent for the caller.
        var echoed = backup with {
            Enabled = command.Enabled,
            Cron = cron,
            Time = cronChanged ? null : backup.Time,
            InstanceName = Coalesce(command.InstanceName, backup.InstanceName),
            RetentionDays = command.RetentionDays,
            RetentionMaxCount = command.RetentionMaxCount,
            EncryptionPassphrase = command.EncryptionPassphrase ?? backup.EncryptionPassphrase,
            HelperImage = helperImage,
            Provider = provider,
            Sftp = sftp with {
                Host = Coalesce(command.SftpHost, sftp.Host),
                Port = command.SftpPort ?? sftp.Port,
                Username = Coalesce(command.SftpUsername, sftp.Username),
                Password = command.SftpPassword ?? sftp.Password,
                PrivateKey = command.SftpPrivateKey ?? sftp.PrivateKey,
                PrivateKeyPassphrase = command.SftpPrivateKeyPassphrase ?? sftp.PrivateKeyPassphrase,
                BasePath = Coalesce(command.SftpBasePath, sftp.BasePath) ?? "",
            },
            Local = backup.Local with {
                BasePath = Coalesce(command.LocalBasePath, backup.Local.BasePath) ?? "",
            },
        };

        // Recorded post-write so the trail answers "what was the configuration at that time" —
        // effective values only, secrets reduced to which of them were replaced or cleared.
        var updatedSecrets = new[] {
                (command.EncryptionPassphrase, "encryption passphrase"),
                (command.SftpPassword, "SFTP password"),
                (command.SftpPrivateKey, "SFTP private key"),
                (command.SftpPrivateKeyPassphrase, "SFTP key passphrase"),
            }
            .Where(s => s.Item1 is not null)
            .Select(s => s.Item2)
            .ToList();
        var detail =
            $"schedule {(echoed.Enabled ? "on" : "off")}, {cron} ({BackupSchedule.Describe(cron)})"
            + $" · provider {provider}"
            + $" · {BackupService.RetentionSummary(echoed)}"
            + (string.IsNullOrEmpty(echoed.EncryptionPassphrase) ? "" : " · encrypted")
            + (updatedSecrets.Count > 0 ? $" · secrets updated: {string.Join(", ", updatedSecrets)}" : "");
        await audit.RecordAsync(BackupService.AuditCategory, "config.update", "backup settings", detail,
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(BackupConfigDto.From(echoed, pins));
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
