using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Deletes a named access rule. Refused while any route still attaches it, naming the routes.
/// </summary>
/// <remarks>
/// The refusal is the schema's <c>Restrict</c> foreign key made legible (ADR-0039): deleting a rule that
/// hostnames still name would change what each of them admits at the next reconcile — widening some to the
/// instance-wide settings and closing others to nobody, depending on what else they attach. Detaching it
/// first is the same work made visible, and each step is separately auditable.
/// <para>
/// The rule's own clauses cascade, because a clause outside a rule names a subject for nothing.
/// </para>
/// </remarks>
[Handler("proxy.deleteAccessRule")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class DeleteAccessRule(WatchtowerDbContext db)
    : IHandler<DeleteAccessRule.Command, Result<DeleteAccessRule.Response>> {
    public sealed record Command(int Id);

    public sealed record Response(int Id);

    /// <summary>How many attached routes to name before the message says "and N more".</summary>
    private const int MaxNamedRoutes = 5;

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var rule = await db.AccessRules.FirstOrDefaultAsync(r => r.Id == command.Id, ct);
        if (rule is null) return AppError.NotFound($"Access rule {command.Id} not found.");

        // Names rather than a count: "detach it from these two hostnames" is actionable where "2 routes use
        // it" sends the operator looking for them. Ordered after materialising, because DisplayAddress is a
        // computed property and an explicit comparer is not something EF can translate.
        var attachedRoutes = await db.RouteAccessRules.AsNoTracking()
            .Where(a => a.AccessRuleId == rule.Id)
            .Select(a => a.Route!)
            .ToListAsync(ct);
        var attached = attachedRoutes
            .Select(r => r.Domain ?? r.DisplayAddress)
            .OrderBy(d => d, StringComparer.Ordinal)
            .ToList();
        if (attached.Count > 0) {
            var named = string.Join(", ", attached.Take(MaxNamedRoutes));
            var rest = attached.Count > MaxNamedRoutes ? $" and {attached.Count - MaxNamedRoutes} more" : "";
            return AppError.Conflict(
                $"Access rule '{rule.Name}' is still attached to {named}{rest}. "
                + "Detach it from those routes first — deleting it would change who they admit.");
        }

        db.AccessRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return new Response(command.Id);
    }
}
