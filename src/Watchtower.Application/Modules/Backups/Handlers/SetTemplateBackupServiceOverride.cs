using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// Sets (or clears) the per-service backup override every tenant of a template <em>inherits</em> — the
/// template-level twin of <c>backups.setServiceOverride</c>, and the write side the
/// <c>template_backup_service_overrides</c> table shipped without (design.md §"Backups across tenants").
/// </summary>
/// <remarks>
/// <para>
/// Deliberately the same contract as the stack setter, field for field: the whole override is replaced,
/// every knob not supplied is cleared, and an override with nothing set is deleted. Two setters that
/// agreed about the values but disagreed about what an omitted field means is exactly how the ladder
/// starts lying, and the two forms that post them are the same control.
/// </para>
/// <para>
/// Like <c>backups.setTemplatePolicy</c>, this is <b>not</b> a fan-out: it writes one row and the tenants
/// read it live (invariant 18). A tenant's own row for the same service replaces this one <em>whole</em>
/// — precedence is per service, not per knob — and a compose label still beats both.
/// </para>
/// </remarks>
[Handler("backups.setTemplateServiceOverride")]
public sealed class SetTemplateBackupServiceOverride(
    WatchtowerDbContext db, AuditLog audit, ICurrentUser currentUser)
    : IHandler<SetTemplateBackupServiceOverride.Command, Result<SetTemplateBackupServiceOverride.Response>> {
    /// <param name="TemplateId">The template whose fleet-wide override to write.</param>
    /// <param name="Service">The compose service name.</param>
    /// <param name="Exclude">Stands in for <c>watchtower.backup.exclude=true</c>.</param>
    /// <param name="Stop"><c>true</c>, <c>false</c>, <c>pause</c> or null.</param>
    /// <param name="Dump"><c>false</c>, <c>postgres</c> or null.</param>
    public sealed record Command(
        int TemplateId, string Service, bool Exclude = false, string? Stop = null, string? Dump = null);

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

        var template = await db.StackTemplates.FirstOrDefaultAsync(t => t.Id == command.TemplateId, ct);
        if (template is null)
            return AppError.NotFound($"Template {command.TemplateId} not found");

        var wanted = new BackupServiceOverride(command.Exclude, stop, dump);
        var row = await db.TemplateBackupServiceOverrides
            .FirstOrDefaultAsync(o => o.TemplateId == template.Id && o.Service == service, ct);
        if (wanted.IsEmpty) {
            if (row is not null) db.TemplateBackupServiceOverrides.Remove(row);
        } else if (row is null) {
            db.TemplateBackupServiceOverrides.Add(new TemplateBackupServiceOverride {
                TemplateId = template.Id, Service = service, Exclude = wanted.Exclude, Stop = wanted.Stop, Dump = wanted.Dump,
            });
        } else {
            row.Exclude = wanted.Exclude;
            row.Stop = wanted.Stop;
            row.Dump = wanted.Dump;
        }
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(BackupService.AuditCategory, "template.service-override.update", template.Name,
            wanted.IsEmpty
                ? $"service '{service}': override cleared"
                : $"service '{service}': "
                    + string.Join(", ", new[] {
                        wanted.Exclude ? "exclude" : null,
                        wanted.Stop is { } s ? $"stop={s}" : null,
                        wanted.Dump is { } d ? $"dump={d}" : null,
                    }.Where(p => p is not null)),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        // `FromTemplate: true` on the way out, so the caller labels the row it just wrote the same way
        // every tenant's plan preview will ("Template policy: …") rather than as a stack override.
        return new Response(wanted.IsEmpty
            ? null
            : BackupServiceOverrideDto.From(service, wanted with { FromTemplate = true }));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
