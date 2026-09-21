using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Resolves the <see cref="AccessRule"/>s attached to a route into the flat allow-list an enforcement point
/// can apply (ADR-0039). One place, because the answer has to be the same wherever it is asked.
/// </summary>
/// <remarks>
/// Resolution is where the <see cref="AccessClauseKind"/> vocabulary meets the fact that an edge matches on
/// email addresses: a <see cref="AccessClauseKind.User"/> clause becomes that account's address and a
/// <see cref="AccessClauseKind.Group"/> clause becomes its members' — which is why a group removal takes
/// effect at the next reconcile rather than at the next request under the Cloudflare provider.
/// <para>
/// The realm invariant applies here exactly as it does to grants (ADR-0040 decision 3, design.md §13.5):
/// only accounts of the <em>route's own realm</em> contribute an address. A rule is realm-scoped and its
/// account clauses are checked against that realm when written, so this is belt and braces for the case a
/// write cannot catch — a stack that changed category after the attachment, moving the route's population
/// out from under it.
/// </para>
/// </remarks>
public static class AccessRuleResolver {
    /// <summary>
    /// What one route's attached rules admit, flattened and de-duplicated. The four lists are the four
    /// shapes an Access application can carry; <see cref="Emails"/> and <see cref="EmailDomains"/> become
    /// inline includes, the two external lists are attached by reference.
    /// </summary>
    /// <param name="RuleNames">
    /// The attached rules' names, in attachment order — for the warnings, the audit detail and the Routes
    /// page, so an operator reading "denied everyone" can see which rule was supposed to prevent that.
    /// </param>
    /// <param name="UnsupportedKinds">
    /// Clause kinds present on the attached rules that the enforcement point being resolved for cannot
    /// honour. Empty on every path a write went through (they are refused there), so a non-empty value
    /// means the route or the provider changed underneath an existing attachment.
    /// </param>
    public sealed record ResolvedAccess(
        string[] Emails,
        string[] EmailDomains,
        string[] ExternalGroupIds,
        string[] ExternalPolicyIds,
        string[] RuleNames,
        AccessClauseKind[] UnsupportedKinds) {
        /// <summary>Whether anything at all could pass this allow-list.</summary>
        public bool IsEmpty => Emails.Length == 0 && EmailDomains.Length == 0
            && ExternalGroupIds.Length == 0 && ExternalPolicyIds.Length == 0;
    }

    /// <summary>
    /// The resolved allow-list of every route in <paramref name="routeIds"/> that has attachments, keyed by
    /// route id. A route with no attachments is <b>absent</b> rather than present-and-empty: the two mean
    /// different things (ADR-0039 decision 2 — fall back to the instance-wide settings, versus admit
    /// nobody), and collapsing them is how a hostname ends up admitting people nobody chose.
    /// </summary>
    public static async Task<IReadOnlyDictionary<int, ResolvedAccess>> ResolveAsync(
        WatchtowerDbContext db,
        IReadOnlyCollection<int> routeIds,
        AccessEnforcementPoint point,
        CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(routeIds);
        if (routeIds.Count == 0) return new Dictionary<int, ResolvedAccess>();

        var attachments = await db.RouteAccessRules.AsNoTracking()
            .Where(a => routeIds.Contains(a.RouteId))
            .OrderBy(a => a.RouteId).ThenBy(a => a.Order).ThenBy(a => a.Id)
            .Select(a => new { a.RouteId, a.AccessRuleId, RuleName = a.AccessRule!.Name })
            .ToListAsync(ct);
        if (attachments.Count == 0) return new Dictionary<int, ResolvedAccess>();

        var ruleIds = attachments.Select(a => a.AccessRuleId).Distinct().ToList();
        var clauses = await db.AccessRuleClauses.AsNoTracking()
            .Where(c => ruleIds.Contains(c.AccessRuleId))
            .OrderBy(c => c.Order).ThenBy(c => c.Id)
            .Select(c => new ClauseRow(
                c.AccessRuleId, c.Kind, c.Value,
                // Resolved in the same round trip rather than in a second pass: the address and the realm
                // are what the clause contributes, and a disabled account contributes nothing at all —
                // the rule LoadGrantedEmailsAsync applies to grants.
                c.UserId == null ? null : (c.User!.Disabled ? null : c.User.Email),
                c.UserId == null ? null : (int?)c.User!.RealmId,
                c.GroupId))
            .ToListAsync(ct);

        // Member addresses of every group any attached rule names, with the member's realm, so the same
        // invariant can be applied to a group clause as to a direct one.
        var groupIds = clauses.Where(c => c.GroupId is not null).Select(c => c.GroupId!.Value).Distinct().ToList();
        var groupMembers = groupIds.Count == 0
            ? []
            : await db.GroupMembers.AsNoTracking()
                .Where(m => groupIds.Contains(m.GroupId))
                .Where(m => !m.User!.Disabled)
                .Select(m => new { m.GroupId, m.User!.Email, m.User.RealmId })
                .ToListAsync(ct);
        var membersByGroup = groupMembers
            .GroupBy(m => m.GroupId)
            .ToDictionary(g => g.Key, g => g.Select(m => (m.Email, m.RealmId)).ToArray());

        var clausesByRule = clauses.GroupBy(c => c.AccessRuleId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        var routeRealms = await RouteAccessPolicy.RouteRealmIdsAsync(db, routeIds, ct);

        var resolved = new Dictionary<int, ResolvedAccess>();
        foreach (var byRoute in attachments.GroupBy(a => a.RouteId)) {
            // A route whose realm cannot be determined has no population to compare an account against, so
            // every account clause contributes nothing — the fail-closed reading, and the same one
            // LoadGrantedEmailsAsync takes.
            var routeRealmId = routeRealms.TryGetValue(byRoute.Key, out var realmId) ? (int?)realmId : null;
            var emails = new List<string>();
            var emailDomains = new List<string>();
            var externalGroupIds = new List<string>();
            var externalPolicyIds = new List<string>();
            var unsupported = new List<AccessClauseKind>();

            foreach (var attachment in byRoute) {
                if (!clausesByRule.TryGetValue(attachment.AccessRuleId, out var ruleClauses)) continue;
                foreach (var clause in ruleClauses) {
                    if (!AccessClauseSupport.IsSupportedAt(clause.Kind, point)) {
                        unsupported.Add(clause.Kind);
                        continue;
                    }
                    switch (clause.Kind) {
                        case AccessClauseKind.User:
                            if (Admits(clause.UserRealmId) && Trimmed(clause.UserEmail) is { } address)
                                emails.Add(address);
                            break;
                        case AccessClauseKind.Group:
                            if (clause.GroupId is { } groupId && membersByGroup.TryGetValue(groupId, out var members)) {
                                foreach (var (memberEmail, memberRealmId) in members) {
                                    if (Admits(memberRealmId) && Trimmed(memberEmail) is { } memberAddress)
                                        emails.Add(memberAddress);
                                }
                            }
                            break;
                        case AccessClauseKind.Email:
                            if (Trimmed(clause.Value) is { } email) emails.Add(email);
                            break;
                        case AccessClauseKind.EmailDomain:
                            if (Trimmed(clause.Value) is { } domain) emailDomains.Add(domain);
                            break;
                        case AccessClauseKind.ExternalGroup:
                            if (Trimmed(clause.Value) is { } externalGroupId) externalGroupIds.Add(externalGroupId);
                            break;
                        case AccessClauseKind.ExternalPolicy:
                            if (Trimmed(clause.Value) is { } policyId) externalPolicyIds.Add(policyId);
                            break;
                        default:
                            // Unreachable: an unknown kind is already unsupported everywhere above.
                            unsupported.Add(clause.Kind);
                            break;
                    }
                }
            }

            resolved[byRoute.Key] = new ResolvedAccess(
                // Sorted and de-duplicated so a reconcile that changed nothing produces an identical
                // request and writes nothing — the same discipline ProjectAccessApps applies to the
                // instance-wide lists. Emails and domains are case-blind; the opaque ids are not, being
                // somebody else's identifiers.
                Dedupe(emails, StringComparer.OrdinalIgnoreCase),
                Dedupe(emailDomains, StringComparer.OrdinalIgnoreCase),
                Dedupe(externalGroupIds, StringComparer.Ordinal),
                Dedupe(externalPolicyIds, StringComparer.Ordinal),
                [.. byRoute.Select(a => a.RuleName)],
                [.. unsupported.Distinct().Order()]);

            bool Admits(int? subjectRealmId) =>
                routeRealmId is { } route && subjectRealmId is { } subject && route == subject;
        }

        return resolved;

        static string? Trimmed(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        static string[] Dedupe(List<string> values, StringComparer comparer) =>
            [.. values.Distinct(comparer).OrderBy(v => v, comparer)];
    }

    /// <summary>The projection one clause row contributes, flattened out of EF so the loop above is pure.</summary>
    private sealed record ClauseRow(
        int AccessRuleId,
        AccessClauseKind Kind,
        string? Value,
        string? UserEmail,
        int? UserRealmId,
        int? GroupId);
}
