using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Lists the backups actually present on the configured storage for one stack, newest first — the
/// restore picker's source of truth. The local history (<c>backups.events</c>) is NOT it: retention
/// deletes files behind old events, and files may exist that no event of this database remembers.
/// Only files matching Watchtower's naming pattern are returned.
/// </summary>
[Handler("backups.listRemote")]
public sealed class ListRemoteBackups(
    WatchtowerDbContext db,
    IOptionsMonitor<WatchtowerOptions> options,
    BackupStorageFactory storageFactory)
    : IHandler<ListRemoteBackups.Query, Result<ListRemoteBackups.Response>> {
    public sealed record Query(int StackId);

    public sealed record Response(IReadOnlyList<BackupRemoteFileDto> Files);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var stack = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == query.StackId)
            .Select(s => new { s.Name })
            .FirstOrDefaultAsync(ct);
        if (stack is null)
            return AppError.NotFound($"Stack {query.StackId} not found");

        var backup = options.CurrentValue.Backup;
        try {
            using var storage = storageFactory.Create(backup);
            var directory = BackupNaming.StackDirectory(backup.ResolveInstanceName(), stack.Name);
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
            return new Response(files);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            return AppError.Validation($"Could not list the backup storage: {ex.Message}");
        }
    }
}
