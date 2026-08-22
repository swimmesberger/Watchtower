using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Updates a stack's backup participation: whether the schedule includes it, whether its containers
/// are stopped for a consistent snapshot (ADR-0016 §2), and its optional schedule override — a
/// five-field cron expression replacing the instance-wide one for this stack (ADR-0018); null or
/// blank means "follow the instance schedule". The scheduler's cursor is left alone on purpose: a
/// window that opened shortly before the change runs once under the misfire grace, like any other
/// late window, rather than being silently dropped.
/// </summary>
[Handler("backups.setStackConfig")]
public sealed class SetStackBackupConfig(WatchtowerDbContext db, AuditLog audit, ICurrentUser currentUser)
    : IHandler<SetStackBackupConfig.Command, Result<SetStackBackupConfig.Response>> {
    public sealed record Command(int StackId, bool Enabled, bool StopContainers, string? Cron = null);

    public sealed record Response(BackupStackConfigDto Config);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var cron = string.IsNullOrWhiteSpace(command.Cron) ? null : command.Cron.Trim();
        if (cron is not null && !BackupSchedule.TryParse(cron, out _, out var cronError))
            return AppError.Validation(cronError);

        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.Id == command.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        stack.BackupEnabled = command.Enabled;
        stack.BackupStopContainers = command.StopContainers;
        stack.BackupCron = cron;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(BackupService.AuditCategory, "stack.config.update", stack.Name,
            (command.Enabled ? "backups on" : "backups off")
            + (cron is null ? " · schedule: instance default" : $" · schedule {cron} ({BackupSchedule.Describe(cron)})")
            + (command.StopContainers ? " · stop containers for snapshot" : " · keep containers running"),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(new BackupStackConfigDto(stack.Id, stack.BackupEnabled, stack.BackupStopContainers, stack.BackupCron));
    }
}
