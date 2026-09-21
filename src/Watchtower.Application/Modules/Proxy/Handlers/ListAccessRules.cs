using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Lists the named access rules with their clauses, portability and attachment counts (ADR-0039) — the
/// roster an administrator picks from when saying who a route admits.
/// </summary>
/// <remarks>
/// Unfiltered by default for the reason <c>groups.list</c> gives: the caller is an instance administrator,
/// and a default that hid other populations' rules would hide them from the person responsible for them.
/// <para>
/// Same <c>[RequireRole("Admin")]</c> as the rest of the access surface — a rule is an allow-list, so
/// enumerating one is as privileged as reading a route's grants.
/// </para>
/// </remarks>
[Handler("proxy.listAccessRules")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListAccessRules(WatchtowerDbContext db)
    : IHandler<ListAccessRules.Query, Result<ListAccessRules.Response>> {
    /// <summary>Optional realm filter; omitted means every realm.</summary>
    public sealed record Query(int? RealmId = null);

    public sealed record Response(IReadOnlyList<AccessRuleDto> Rules);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var rules = db.AccessRules.AsNoTracking().AsQueryable();
        if (query.RealmId is { } realmId) rules = rules.Where(r => r.RealmId == realmId);

        // Two reads rather than one projection with a nested collection: the clause rows need their
        // subjects' display names joined in, and EF renders that far better as a flat second query than as
        // a correlated sub-select per rule.
        var rows = await rules
            .OrderBy(r => r.Name)
            .Select(r => new {
                r.Id, r.Name, r.RealmId, r.Description,
                AttachedRouteCount = db.RouteAccessRules.Count(a => a.AccessRuleId == r.Id),
            })
            .ToListAsync(ct);
        if (rows.Count == 0) return new Response([]);

        var ruleIds = rows.Select(r => r.Id).ToList();
        var clauses = await db.AccessRuleClauses.AsNoTracking()
            .Where(c => ruleIds.Contains(c.AccessRuleId))
            .OrderBy(c => c.Order).ThenBy(c => c.Id)
            .Select(c => new {
                c.AccessRuleId, c.Kind, c.UserId, c.GroupId, c.Value,
                UserName = c.User!.UserName, GroupName = c.Group!.Name,
            })
            .ToListAsync(ct);
        var clausesByRule = clauses.GroupBy(c => c.AccessRuleId)
            .ToDictionary(g => g.Key, g => g.ToArray());

        var dtos = new List<AccessRuleDto>(rows.Count);
        foreach (var row in rows) {
            var ruleClauses = clausesByRule.TryGetValue(row.Id, out var found) ? found : [];
            var (inProcess, cloudflare) = AccessRuleMapping.Portability(ruleClauses.Select(c => c.Kind));
            dtos.Add(new AccessRuleDto(
                row.Id, row.Name, row.RealmId, row.Description,
                [.. ruleClauses.Select(c => new AccessRuleClauseDto(
                    c.Kind, c.UserId, c.GroupId, c.Value,
                    AccessRuleMapping.LabelFor(c.Kind, c.UserName, c.GroupName, c.Value)))],
                inProcess, cloudflare, row.AttachedRouteCount));
        }

        return new Response(dtos);
    }
}
