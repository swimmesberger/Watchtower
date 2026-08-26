using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Everything the product Backups tab renders above its history: the tenancy templates' inheritable
/// backup policies and one rollup of how the product's deployments are actually doing
/// (design.md §"Backups across tenants" — "19 backed up in last 24 h · 1 failed · 2 never").
/// </summary>
/// <remarks>
/// One call rather than two because the two halves are read together and neither is useful alone: a
/// policy card with no rollup cannot say whether the policy is working, and a rollup with no policy
/// leaves nowhere to act. The fleet <em>history</em> is the separate <c>backups.events(productId:)</c>
/// call, because it pages and refetches on its own cadence.
/// </remarks>
[Handler("backups.getProductBackups")]
public sealed class GetProductBackups(WatchtowerDbContext db, IOptionsMonitor<WatchtowerOptions> options)
    : IHandler<GetProductBackups.Query, Result<GetProductBackups.Response>> {
    /// <summary>How far back "backed up recently" reaches. The design's number, in one place.</summary>
    public const int RollupWindowHours = 24;

    public sealed record Query(int ProductId);

    /// <param name="Templates">
    /// One entry per tenancy template of the product, in name order. Usually zero (a standalone product)
    /// or one; a product with several templates gets several cards rather than a merged policy that
    /// belongs to none of them.
    /// </param>
    /// <param name="Rollup">How the product's deployments are doing.</param>
    /// <param name="InstanceCron">
    /// The instance-wide expression the <c>instance</c> rung resolves to, so the card can spell out what
    /// "follow the instance schedule" means without a second round trip to <c>backups.getConfig</c>.
    /// </param>
    /// <param name="ScheduleEnabled">
    /// The instance master switch. False means nothing runs on a schedule at all, whatever the policy
    /// says — the card has to lead with that rather than showing a schedule that never fires.
    /// </param>
    public sealed record Response(
        IReadOnlyList<BackupTemplatePolicyDto> Templates,
        BackupProductRollupDto Rollup,
        string InstanceCron,
        bool ScheduleEnabled);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        if (!await db.Products.AnyAsync(p => p.Id == query.ProductId, ct))
            return AppError.NotFound($"Product {query.ProductId} not found.");

        var templates = await db.StackTemplates.AsNoTracking()
            .Where(t => t.ProductId == query.ProductId)
            .OrderBy(t => t.Name)
            .ToListAsync(ct);

        // "Overrides the policy" is any of the four fields set on the tenant — that is exactly what makes
        // a fleet-policy edit not reach it, so it is the number the card warns with.
        var overriddenByTemplate = await db.Stacks.AsNoTracking()
            .Where(s => s.TemplateId != null && s.Template!.ProductId == query.ProductId)
            .GroupBy(s => s.TemplateId!.Value)
            .Select(g => new {
                TemplateId = g.Key,
                Tenants = g.Count(),
                Overridden = g.Count(s =>
                    s.BackupEnabled != null || s.BackupStopContainers != null
                    || s.BackupCron != null || s.BackupQuiesceMode != null),
            })
            .ToDictionaryAsync(x => x.TemplateId, ct);

        var policies = new List<BackupTemplatePolicyDto>(templates.Count);
        foreach (var template in templates) {
            overriddenByTemplate.TryGetValue(template.Id, out var counts);
            policies.Add(BackupTemplatePolicyDto.From(
                template, counts?.Tenants ?? 0, counts?.Overridden ?? 0));
        }

        var since = DateTimeOffset.UtcNow.AddHours(-RollupWindowHours);
        // The stack and its template come back with the three per-stack event facts in one round trip:
        // enrolment is the resolved policy's (BackupPolicyResolver, invariant 18), so the ladder cannot
        // be re-derived here and disagree with what the scheduler does. "Failed" reads the newest
        // *terminal* run rather than counting failures, because a stack that failed at 03:30 and was
        // backed up by hand at 09:00 is not a failing stack — and a run that is queued or still running
        // is not yet an answer to anything, so it is skipped rather than treated as either.
        var rows = await db.Stacks.AsNoTracking()
            .Where(s => s.ProductId == query.ProductId)
            .Select(s => new {
                Stack = s,
                s.Template,
                Recent = db.BackupEvents.Any(e =>
                    e.StackId == s.Id && e.Status == BackupStatuses.Success && e.StartedAt >= since),
                Ever = db.BackupEvents.Any(e => e.StackId == s.Id && e.Status == BackupStatuses.Success),
                LatestTerminal = db.BackupEvents
                    .Where(e => e.StackId == s.Id
                        && (e.Status == BackupStatuses.Success || e.Status == BackupStatuses.Failed))
                    .OrderByDescending(e => e.StartedAt)
                    .ThenByDescending(e => e.Id)
                    .Select(e => e.Status)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        // One pass, first match wins — which is what makes the four buckets a partition rather than
        // four overlapping counts. Never outranks Failed because a stack that has never been backed up
        // is fully described by that; Failed outranks BackedUpRecently because a success followed by a
        // failure inside the same window is a stack an operator has to look at.
        var enrolled = 0;
        var never = 0;
        var failed = 0;
        var recent = 0;
        var stale = 0;
        foreach (var row in rows) {
            if (!BackupPolicyResolver.Resolve(row.Stack, row.Template).Enabled) continue;
            enrolled++;
            if (!row.Ever) never++;
            else if (row.LatestTerminal == BackupStatuses.Failed) failed++;
            else if (row.Recent) recent++;
            else stale++;
        }

        var rollup = new BackupProductRollupDto(
            rows.Count, enrolled, rows.Count - enrolled, recent, stale, failed, never, RollupWindowHours);

        var backup = options.CurrentValue.Backup;
        return new Response(
            policies, rollup, BackupSchedule.ResolveGlobalExpression(backup), backup.Enabled);
    }
}
