using System.Text;
using Elarion.Abstractions.Identity;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Probes the currently configured storage backend by writing and deleting a tiny file under the
/// instance's directory — connectivity, auth and write permission all fail here with the backend's
/// own words, instead of later as a scheduled-run failure nobody is watching for. Save the settings
/// first; the probe reads the stored configuration.
/// </summary>
[Handler("backups.testStorage")]
public sealed class TestBackupStorage(
    IOptionsMonitor<WatchtowerOptions> options,
    BackupStorageFactory storageFactory,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<TestBackupStorage.Command, Result<TestBackupStorage.Response>> {
    public sealed record Command;

    /// <summary>The probed target's description, e.g. <c>sftp://u123@host:23/backups</c>.</summary>
    public sealed record Response(string Description);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var backup = options.CurrentValue.Backup;
        var actor = await audit.ActorAsync(currentUser, ct);
        try {
            using var storage = storageFactory.Create(backup);
            var probePath = $"{BackupNaming.Sanitize(backup.ResolveInstanceName())}/.watchtower-storage-probe";
            await storage.UploadAsync(probePath, async (stream, uploadCt) => {
                await stream.WriteAsync(Encoding.UTF8.GetBytes("watchtower storage probe"), uploadCt);
            }, ct);
            await storage.DeleteFileAsync(probePath, ct);
            await audit.RecordAsync(BackupService.AuditCategory, "storage.test", storage.Description,
                "probe file written and deleted", actor: actor, ct: ct);
            return new Response(storage.Description);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // Target by provider kind — the descriptive target may be exactly what failed to build.
            var provider = backup.ResolveProvider() == BackupProviderKind.Local ? "local" : "sftp";
            await audit.RecordAsync(BackupService.AuditCategory, "storage.test", provider, null,
                success: false, error: ex.Message, actor: actor, ct: ct);
            return AppError.Validation($"Storage test failed: {ex.Message}");
        }
    }
}
