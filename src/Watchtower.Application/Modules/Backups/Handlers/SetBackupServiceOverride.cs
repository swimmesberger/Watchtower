using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Sets (or clears) the per-service backup override of one compose service of a stack (ADR-0020) —
/// the UI's counterpart to the <c>watchtower.backup.*</c> labels, in the labels' own value syntax. The
/// whole override is replaced: every knob not supplied is cleared, and an override with nothing set is
/// deleted. A label on the deployed service keeps winning; the handler does not refuse an override
/// that is currently shadowed by a label, because labels come and go with deploys and the preview
/// shows which one is in effect.
/// </summary>
[Handler("backups.setServiceOverride")]
public sealed class SetBackupServiceOverride(WatchtowerDbContext db, AuditLog audit, ICurrentUser currentUser)
    : IHandler<SetBackupServiceOverride.Command, Result<SetBackupServiceOverride.Response>> {
    /// <param name="Service">The compose service name.</param>
    /// <param name="Exclude">Stands in for <c>watchtower.backup.exclude=true</c>.</param>
    /// <param name="Stop"><c>true</c>, <c>false</c>, <c>pause</c> or null.</param>
    /// <param name="Dump"><c>false</c>, <c>postgres</c> or null.</param>
    public sealed record Command(int StackId, string Service, bool Exclude = false, string? Stop = null, string? Dump = null);

    /// <param name="Override">The stored override, or null when it was cleared.</param>
    public sealed record Response(BackupServiceOverrideDto? Override);

    private static readonly string[] StopValues = ["true", "false", BackupPlan.StopLabelPause];
    private static readonly string[] DumpValues = ["false", "postgres"];

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var service = command.Service.Trim();
        if (service.Length is 0 or > 128)
            return AppError.Validation("Service name is required (at most 128 characters).");
        var stop = Normalize(command.Stop);
        if (stop is not null && !StopValues.Contains(stop, StringComparer.Ordinal))
            return AppError.Validation($"Unknown stop value '{command.Stop}' — expected \"true\", \"false\" or \"pause\".");
        var dump = Normalize(command.Dump);
        if (dump is not null && !DumpValues.Contains(dump, StringComparer.Ordinal))
            return AppError.Validation($"Unknown dump value '{command.Dump}' — expected \"false\" or \"postgres\".");

        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.Id == command.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        var wanted = new BackupServiceOverride(command.Exclude, stop, dump);
        var row = await db.StackBackupServiceOverrides
            .FirstOrDefaultAsync(o => o.StackId == stack.Id && o.Service == service, ct);
        if (wanted.IsEmpty) {
            if (row is not null) db.StackBackupServiceOverrides.Remove(row);
        } else if (row is null) {
            db.StackBackupServiceOverrides.Add(new StackBackupServiceOverride {
                StackId = stack.Id, Service = service, Exclude = wanted.Exclude, Stop = wanted.Stop, Dump = wanted.Dump,
            });
        } else {
            row.Exclude = wanted.Exclude;
            row.Stop = wanted.Stop;
            row.Dump = wanted.Dump;
        }
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(BackupService.AuditCategory, "stack.service-override.update", stack.Name,
            wanted.IsEmpty
                ? $"service '{service}': override cleared"
                : $"service '{service}': "
                    + string.Join(", ", new[] {
                        wanted.Exclude ? "exclude" : null,
                        wanted.Stop is { } s ? $"stop={s}" : null,
                        wanted.Dump is { } d ? $"dump={d}" : null,
                    }.Where(p => p is not null)),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(wanted.IsEmpty ? null : BackupServiceOverrideDto.From(service, wanted));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
