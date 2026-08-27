using Elarion.Abstractions.Authorization;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Lists the archives of Watchtower's own database present on the configured storage, newest first
/// (ADR-0027) — the instance counterpart of <see cref="ListRemoteBackups"/>, and for the same reason the
/// storage rather than <c>backups.events</c> is the source of truth: retention deletes files behind old
/// events, and an archive written by a different instance of this Watchtower is still restorable here.
/// </summary>
[Handler("backups.listInstance")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListInstanceBackups(
    IOptionsMonitor<WatchtowerOptions> options,
    BackupStorageFactory storageFactory)
    : IHandler<ListInstanceBackups.Query, Result<ListInstanceBackups.Response>> {
    public sealed record Query;

    /// <param name="Files">The archives, newest first.</param>
    /// <param name="Directory">The provider-relative directory they were listed from.</param>
    public sealed record Response(IReadOnlyList<BackupRemoteFileDto> Files, string Directory);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var backup = options.CurrentValue.Backup;
        var directory = BackupNaming.InstanceDirectory(backup.ResolveInstanceName());
        try {
            using var storage = storageFactory.Create(backup);
            var files = (await storage.ListFilesAsync(directory, ct))
                .Select(f => (File: f, TakenAt: BackupNaming.ParseTimestamp(f.Name)))
                .Where(x => x.TakenAt is not null)
                .OrderByDescending(x => x.TakenAt)
                .Select(x => new BackupRemoteFileDto(
                    x.File.Name,
                    x.File.SizeBytes,
                    x.TakenAt!.Value,
                    Encrypted: x.File.Name.EndsWith(".enc", StringComparison.Ordinal)))
                .ToList();
            return new Response(files, directory);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return AppError.Validation($"Could not list the backup storage: {ex.Message}");
        }
    }
}
