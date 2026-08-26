using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Backs up every tenant of a template — the backup twin of <c>templates.deployAll</c>, and the button
/// an operator presses before a risky fleet change (design.md §"Backups across tenants").
/// </summary>
/// <remarks>
/// <para>
/// <b>Serial, and the caller has to be told so.</b> The backup queue is single-flight process-wide by
/// design (ADR-0016 §6) — runs compete for the same disk, network and Docker daemon, and stopping
/// several stacks at once multiplies the blast radius — so a 20-tenant fan-out is 20 backups one after
/// another, not 20 at once. <see cref="Response.Count"/> is therefore the number <em>queued</em>, and
/// the UI states the duration expectation next to it (design.md §Risks, open question 12).
/// </para>
/// <para>
/// It lives in the Backups module although it is named <c>templates.*</c>: the operation is a backup,
/// it audits under <c>backups</c>, and gating it on the Backups module is what should happen when
/// backups are switched off. Tenants that already have a backup queued coalesce onto it, which is why
/// the response reports what the queue accepted rather than the tenant count.
/// </para>
/// </remarks>
[Handler("templates.backupAll")]
public sealed class BackupAllTenants(
    WatchtowerDbContext db, BackupQueueService queue, AuditLog audit, ICurrentUser currentUser)
    : IHandler<BackupAllTenants.Command, Result<BackupAllTenants.Response>> {
    public sealed record Command(int TemplateId);

    /// <param name="Count">How many tenants were queued (distinct backup runs, after coalescing).</param>
    /// <param name="BackupEventIds">The tracking event per tenant, in stack-id order; duplicates when two coalesced.</param>
    public sealed record Response(int Count, IReadOnlyList<int> BackupEventIds);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var template = await db.StackTemplates.AsNoTracking()
            .Where(t => t.Id == command.TemplateId)
            .Select(t => new { t.Id, t.Name })
            .FirstOrDefaultAsync(ct);
        if (template is null)
            return AppError.NotFound($"Template {command.TemplateId} not found");

        var stackIds = await db.Stacks.AsNoTracking()
            .Where(s => s.TemplateId == template.Id)
            .OrderBy(s => s.Id)
            .Select(s => s.Id)
            .ToListAsync(ct);

        var eventIds = new List<int>(stackIds.Count);
        foreach (var stackId in stackIds)
            eventIds.Add(queue.Enqueue(stackId, BackupTriggers.TemplateAll).BackupEventId);

        // One row for the fan-out, per design.md §Audit — not one per tenant, which would bury the
        // decision under its own consequences.
        await audit.RecordAsync(BackupService.AuditCategory, "backup.all", template.Name,
            $"{eventIds.Distinct().Count()} instance(s) queued — the backup queue runs them one at a time",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(eventIds.Distinct().Count(), eventIds);
    }
}
