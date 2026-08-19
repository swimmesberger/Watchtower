using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Enqueues a backup of one stack on the single-flight backup queue (ADR-0016 §6) and returns the
/// tracking event immediately. Works regardless of the schedule master switch — an explicit run is
/// an operator's decision — but still requires a configured storage provider (the run fails with
/// the provider's message otherwise).
/// </summary>
[Handler("backups.run")]
public sealed class RunBackup(WatchtowerDbContext db, BackupQueueService queue)
    : IHandler<RunBackup.Command, Result<RunBackup.Response>> {
    public sealed record Command(int StackId);

    public sealed record Response(BackupRunAcceptedDto Backup);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (!await db.Stacks.AnyAsync(s => s.Id == command.StackId, ct))
            return AppError.NotFound($"Stack {command.StackId} not found");

        var result = queue.Enqueue(command.StackId, "manual");
        return new Response(new BackupRunAcceptedDto(result.BackupEventId, result.Status));
    }
}
