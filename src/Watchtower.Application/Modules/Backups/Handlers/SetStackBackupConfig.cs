using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Updates a stack's backup participation: whether the schedule includes it, whether its containers
/// are quiesced for a consistent snapshot (ADR-0016 §2), how — <c>stop</c> (default) or <c>pause</c>
/// (crash-consistent, ADR-0019) — and its optional schedule override — a five-field cron expression
/// replacing the instance-wide one for this stack (ADR-0018); null or blank means "follow the
/// instance schedule". The scheduler's cursor is left alone on purpose: a window that opened shortly
/// before the change runs once under the misfire grace, like any other late window, rather than being
/// silently dropped.
/// </summary>
[Handler("backups.setStackConfig")]
public sealed class SetStackBackupConfig(WatchtowerDbContext db, AuditLog audit, ICurrentUser currentUser)
    : IHandler<SetStackBackupConfig.Command, Result<SetStackBackupConfig.Response>> {
    /// <param name="Enabled">Whether the schedule includes this stack; null clears it and inherits.</param>
    /// <param name="StopContainers">Whether the run quiesces the volume writers; null clears it and inherits.</param>
    /// <param name="Cron">A five-field expression; null or blank clears it and inherits.</param>
    /// <param name="QuiesceMode"><c>stop</c>, <c>pause</c>, or null/<c>inherit</c> to clear it.</param>
    public sealed record Command(
        int StackId, bool? Enabled, bool? StopContainers, string? Cron = null, string? QuiesceMode = null);

    public sealed record Response(BackupStackConfigDto Config);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var cron = string.IsNullOrWhiteSpace(command.Cron) ? null : command.Cron.Trim();
        if (cron is not null && !BackupSchedule.TryParse(cron, out _, out var cronError))
            return AppError.Validation(cronError);
        if (!BackupQuiesceModes.TryParse(command.QuiesceMode, out var quiesceMode))
            return AppError.Validation(BackupQuiesceModes.ParseError(command.QuiesceMode));

        var stack = await db.Stacks
            .Include(s => s.Template)
            .FirstOrDefaultAsync(s => s.Id == command.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        stack.BackupEnabled = command.Enabled;
        stack.BackupStopContainers = command.StopContainers;
        stack.BackupQuiesceMode = quiesceMode;
        stack.BackupCron = cron;
        await db.SaveChangesAsync(ct);

        // The trail records what the stack will actually do, not the four raw values — "backups off"
        // and "backups: inherited from template X (off)" are the same outcome today and can diverge
        // tomorrow, and the second is the one an operator reading the trail needs.
        var policy = BackupPolicyResolver.Resolve(stack, stack.Template);
        await audit.RecordAsync(BackupService.AuditCategory, "stack.config.update", stack.Name,
            (policy.Enabled ? "backups on" : "backups off") + Inherited(policy.EnabledSource)
            + (policy.Cron is null
                ? " · schedule: instance default"
                : $" · schedule {policy.Cron} ({BackupSchedule.Describe(policy.Cron)}){Inherited(policy.CronSource)}")
            // The suffix has to describe the field the clause is actually about. With the switch off the
            // clause is the switch's; with it on the clause names the *quiesce mode*, and marking that
            // "(inherited)" from the switch's provenance would say the fleet chose pause when the stack
            // did (or the reverse).
            + (!policy.StopContainers
                ? " · keep containers running" + Inherited(policy.StopContainersSource)
                : (policy.QuiesceMode == BackupQuiesceMode.Pause
                    ? " · pause containers for snapshot (crash-consistent)"
                    : " · stop containers for snapshot")
                  + Inherited(policy.QuiesceModeSource)),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(BackupStackConfigDto.From(stack));
    }

    /// <summary>" (inherited)" for a value the stack did not set itself; nothing otherwise.</summary>
    private static string Inherited(BackupPolicySource source) =>
        source == BackupPolicySource.Stack ? "" : " (inherited)";
}
