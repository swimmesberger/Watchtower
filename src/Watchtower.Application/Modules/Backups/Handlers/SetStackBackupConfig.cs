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
    /// <param name="QuiesceMode"><c>stop</c> or <c>pause</c>; null/blank reads as <c>stop</c>.</param>
    public sealed record Command(
        int StackId, bool Enabled, bool StopContainers, string? Cron = null, string? QuiesceMode = null);

    public sealed record Response(BackupStackConfigDto Config);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var cron = string.IsNullOrWhiteSpace(command.Cron) ? null : command.Cron.Trim();
        if (cron is not null && !BackupSchedule.TryParse(cron, out _, out var cronError))
            return AppError.Validation(cronError);
        if (!BackupQuiesceModes.TryParse(command.QuiesceMode, out var quiesceMode))
            return AppError.Validation(
                $"Unknown quiesce mode '{command.QuiesceMode}' — expected \"{BackupQuiesceModes.Stop}\" or \"{BackupQuiesceModes.Pause}\".");

        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.Id == command.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        stack.BackupEnabled = command.Enabled;
        stack.BackupStopContainers = command.StopContainers;
        stack.BackupQuiesceMode = quiesceMode;
        stack.BackupCron = cron;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(BackupService.AuditCategory, "stack.config.update", stack.Name,
            (command.Enabled ? "backups on" : "backups off")
            + (cron is null ? " · schedule: instance default" : $" · schedule {cron} ({BackupSchedule.Describe(cron)})")
            + (!command.StopContainers ? " · keep containers running"
                : quiesceMode == BackupQuiesceMode.Pause ? " · pause containers for snapshot (crash-consistent)"
                : " · stop containers for snapshot"),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(BackupStackConfigDto.From(stack));
    }
}
