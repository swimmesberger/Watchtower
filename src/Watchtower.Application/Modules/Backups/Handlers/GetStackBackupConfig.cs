using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Returns a stack's backup participation (schedule opt-in, stop-for-snapshot flag, quiesce mode,
/// schedule override) — both the effective values and, since stage 7 of ADR-0026, the stack's own
/// tri-state values and which rung of the ladder decided each effective one.
/// </summary>
[Handler("backups.getStackConfig")]
public sealed class GetStackBackupConfig(WatchtowerDbContext db)
    : IHandler<GetStackBackupConfig.Query, Result<GetStackBackupConfig.Response>> {
    public sealed record Query(int StackId);

    public sealed record Response(BackupStackConfigDto Config);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        // The template is the ladder's third rung — without it every inherited field would report itself
        // as the instance default and the tab's "Set by" labels would be quietly wrong.
        var stack = await db.Stacks.AsNoTracking()
            .Include(s => s.Template)
            .FirstOrDefaultAsync(s => s.Id == query.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {query.StackId} not found");
        return new Response(BackupStackConfigDto.From(stack));
    }
}
