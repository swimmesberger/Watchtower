using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>Returns a stack's backup participation (schedule opt-in, stop-for-snapshot flag, quiesce mode, schedule override).</summary>
[Handler("backups.getStackConfig")]
public sealed class GetStackBackupConfig(WatchtowerDbContext db)
    : IHandler<GetStackBackupConfig.Query, Result<GetStackBackupConfig.Response>> {
    public sealed record Query(int StackId);

    public sealed record Response(BackupStackConfigDto Config);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var stack = await db.Stacks.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == query.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {query.StackId} not found");
        return new Response(BackupStackConfigDto.From(stack));
    }
}
