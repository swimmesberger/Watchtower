using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// The dry run of a backup for one stack (ADR-0020): what the next run would archive, quiesce, dump
/// and skip for the stack as deployed right now, row per container, with the decision's source
/// (mount rule, compose label, UI override) and the planner's warnings. Read-only against the engine —
/// it lists and inspects, never stops. The same preparation the run itself uses, so the tab cannot
/// drift from what happens at 03:30.
/// </summary>
[Handler("backups.previewPlan")]
public sealed class GetBackupPlanPreview(WatchtowerDbContext db, BackupService backups)
    : IHandler<GetBackupPlanPreview.Query, Result<GetBackupPlanPreview.Response>> {
    public sealed record Query(int StackId);

    public sealed record Response(BackupPlanPreviewDto Preview);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var stack = await db.Stacks.AsNoTracking().FirstOrDefaultAsync(s => s.Id == query.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {query.StackId} not found");
        var preview = await backups.PreviewPlanAsync(stack, ct);
        return new Response(BackupPlanPreviewDto.From(preview));
    }
}
