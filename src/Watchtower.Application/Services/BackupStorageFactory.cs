using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// Creates the configured <see cref="IBackupStorage"/> from a snapshot of the backup options
/// (ADR-0016 §3). Mirrors the metrics/proxy router idea in miniature: callers resolve the backend
/// per operation, which is what makes the provider runtime-switchable from the Settings page.
/// A configuration problem surfaces as <see cref="InvalidOperationException"/> with an
/// operator-readable message; callers turn it into a failed event / validation error.
/// </summary>
public sealed class BackupStorageFactory {
    /// <summary>A connected-on-first-use storage for the options snapshot. Caller disposes.</summary>
    public IBackupStorage Create(BackupOptions backup) => backup.ResolveProvider() switch {
        BackupProviderKind.Local when string.IsNullOrWhiteSpace(backup.Local.BasePath) =>
            throw new InvalidOperationException("Local backup storage is not configured: BasePath is empty."),
        BackupProviderKind.Local => new LocalBackupStorage(backup.Local.BasePath.Trim()),
        _ => new SftpBackupStorage(backup.Sftp),
    };
}
