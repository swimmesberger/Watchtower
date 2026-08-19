using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Updates a stack's backup participation: whether the daily schedule includes it, and whether its
/// containers are stopped for a consistent snapshot (ADR-0016 §2).
/// </summary>
[Handler("backups.setStackConfig")]
public sealed class SetStackBackupConfig(WatchtowerDbContext db)
    : IHandler<SetStackBackupConfig.Command, Result<SetStackBackupConfig.Response>> {
    public sealed record Command(int StackId, bool Enabled, bool StopContainers);

    public sealed record Response(BackupStackConfigDto Config);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.Id == command.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        stack.BackupEnabled = command.Enabled;
        stack.BackupStopContainers = command.StopContainers;
        await db.SaveChangesAsync(ct);
        return new Response(new BackupStackConfigDto(stack.Id, stack.BackupEnabled, stack.BackupStopContainers));
    }
}
