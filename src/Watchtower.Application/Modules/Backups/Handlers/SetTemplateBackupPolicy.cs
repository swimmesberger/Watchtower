using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Writes the backup policy every tenant of a template <em>inherits</em> (design.md §"Backups across
/// tenants"). One call, one row — the tenants are untouched, which is the whole point: the next edit
/// reaches them all again, including the ones provisioned since.
/// </summary>
/// <remarks>
/// <para>
/// Every field is tri-state and every field is written on every call, exactly like
/// <c>backups.setStackConfig</c>: the form posts the whole policy, so an omitted field means "clear it"
/// rather than "leave it". Null clears — the template goes back to having no opinion and the instance
/// default applies.
/// </para>
/// <para>
/// Deliberately <em>not</em> a fan-out. A tenant that set a value of its own keeps it (invariant 5's
/// counterpart for policy: inheritance is live, so nothing has to be pushed), and the count of those is
/// on <see cref="BackupTemplatePolicyDto.OverriddenTenantCount"/> so the UI can say the edit will not
/// reach them.
/// </para>
/// </remarks>
[Handler("backups.setTemplatePolicy")]
public sealed class SetTemplateBackupPolicy(WatchtowerDbContext db, AuditLog audit, ICurrentUser currentUser)
    : IHandler<SetTemplateBackupPolicy.Command, Result<SetTemplateBackupPolicy.Response>> {
    /// <param name="TemplateId">The template whose policy to write.</param>
    /// <param name="Enabled">Whether tenants join the backup schedule; null = no opinion.</param>
    /// <param name="StopContainers">Whether a tenant's run quiesces the volume writers; null = no opinion.</param>
    /// <param name="Cron">A five-field expression; null or blank = no opinion (the instance schedule).</param>
    /// <param name="QuiesceMode"><c>stop</c>, <c>pause</c>, or null/<c>inherit</c> for no opinion.</param>
    public sealed record Command(
        int TemplateId,
        bool? Enabled = null,
        bool? StopContainers = null,
        string? Cron = null,
        string? QuiesceMode = null);

    public sealed record Response(BackupTemplatePolicyDto Policy);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var cron = string.IsNullOrWhiteSpace(command.Cron) ? null : command.Cron.Trim();
        if (cron is not null && !BackupSchedule.TryParse(cron, out _, out var cronError))
            return AppError.Validation(cronError);
        if (!BackupQuiesceModes.TryParse(command.QuiesceMode, out var quiesceMode))
            return AppError.Validation(BackupQuiesceModes.ParseError(command.QuiesceMode));

        var template = await db.StackTemplates.FirstOrDefaultAsync(t => t.Id == command.TemplateId, ct);
        if (template is null)
            return AppError.NotFound($"Template {command.TemplateId} not found");

        template.BackupEnabled = command.Enabled;
        template.BackupStopContainers = command.StopContainers;
        template.BackupCron = cron;
        template.BackupQuiesceMode = quiesceMode;
        await db.SaveChangesAsync(ct);

        var tenants = await db.Stacks.CountAsync(s => s.TemplateId == template.Id, ct);
        var overridden = await db.Stacks.CountAsync(s =>
            s.TemplateId == template.Id
            && (s.BackupEnabled != null || s.BackupStopContainers != null
                || s.BackupCron != null || s.BackupQuiesceMode != null), ct);

        // The detail names the reach as well as the values: "backups on" over a fleet where four tenants
        // override the policy is a different fact from "backups on" over one where none do.
        var detail = string.Join(" · ", [
            Describe("backups", command.Enabled switch { true => "on", false => "off", null => null }),
            Describe("schedule", cron is null ? null : $"{cron} ({BackupSchedule.Describe(cron)})"),
            Describe("quiesce", command.StopContainers switch {
                false => "keep containers running",
                true => quiesceMode == Entities.BackupQuiesceMode.Pause ? "pause" : "stop",
                null => null,
            }),
            $"{tenants} instance(s)"
            + (overridden > 0 ? $", {overridden} with their own settings (unchanged)" : ""),
        ]);
        await audit.RecordAsync(BackupService.AuditCategory, "template.policy.update", template.Name, detail,
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(BackupTemplatePolicyDto.From(template, tenants, overridden));
    }

    /// <summary>"label: value" for a field the template sets, "label: inherit" when it does not.</summary>
    private static string Describe(string label, string? value) => $"{label}: {value ?? "inherit"}";
}
