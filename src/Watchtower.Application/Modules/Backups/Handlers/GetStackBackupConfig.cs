using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>Returns a stack's backup participation (schedule opt-in, stop-for-snapshot flag).</summary>
[Handler("backups.getStackConfig")]
public sealed class GetStackBackupConfig(WatchtowerDbContext db)
    : IHandler<GetStackBackupConfig.Query, Result<GetStackBackupConfig.Response>> {
    public sealed record Query(int StackId);

    public sealed record Response(BackupStackConfigDto Config);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var stack = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == query.StackId)
            .Select(s => new { s.Id, s.BackupEnabled, s.BackupStopContainers })
            .FirstOrDefaultAsync(ct);
        if (stack is null)
            return AppError.NotFound($"Stack {query.StackId} not found");
        return new Response(new BackupStackConfigDto(stack.Id, stack.BackupEnabled, stack.BackupStopContainers));
    }
}
