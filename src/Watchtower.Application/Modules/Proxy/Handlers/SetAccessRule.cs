using System.Globalization;
using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Creates or replaces one named access rule and its clause list (ADR-0039) — an upsert rather than a
/// create plus an edit, because a rule is its clauses and there is no useful intermediate state where it
/// has a name and nothing else.
/// </summary>
/// <remarks>
/// Fail-fast like <see cref="SetAccess"/>: every clause is validated — kind, subject, realm — before
/// anything is persisted, so a command naming one good clause and one unknown group is refused whole rather
/// than half-applied. Clauses are reconciled by position rather than deleted and re-added, so re-saving an
/// unchanged rule churns no rows and the projection's next reconcile sees no change.
/// <para>
/// A rule may hold clauses the <em>active</em> provider cannot honour, and that is deliberate (ADR-0039
/// decision 4): staging a move between edges means keeping both spellings on the rule while the cutover
/// happens. What the portability check governs is <see cref="SetAccess"/> — attaching such a rule to a route
/// the active provider would then mis-serve. The response carries the two flags so a UI can say so.
/// </para>
/// </remarks>
[Handler("proxy.setAccessRule")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class SetAccessRule(WatchtowerDbContext db, IProxyProvider proxy, TimeProvider time)
    : IHandler<SetAccessRule.Command, Result<SetAccessRule.Response>> {

    /// <summary>The longest name a rule may carry — the <c>Group</c> limit, for the same reasons.</summary>
    public const int MaxNameLength = 64;

    /// <summary>The longest description; long enough for a sentence, short enough to render in a picker.</summary>
    public const int MaxDescriptionLength = 256;

    /// <summary>
    /// The longest a value-carrying clause may be. Generous — it has to hold an email address, a domain and
    /// somebody else's opaque identifier — and present so a malformed paste cannot become an unbounded row.
    /// </summary>
    public const int MaxValueLength = 320;

    /// <param name="Id">
    /// The rule to replace, or null to create one. Null rather than a separate <c>create</c> method because
    /// the validation, the clause reconcile and the realm rules are identical either way, and two handlers
    /// would mean two places for them to drift apart.
    /// </param>
    /// <param name="RealmId">
    /// The population the rule's account clauses may name. Ignored on an update — a rule's realm is
    /// immutable, because changing it would silently invalidate every clause and every attachment at once.
    /// </param>
    public sealed record Command(
        string Name,
        IReadOnlyList<AccessRuleClauseDto> Clauses,
        int? Id = null,
        string? Description = null,
        int? RealmId = null);

    public sealed record Response(AccessRuleDto Rule);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var name = command.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return AppError.Validation("Access rule name is required.");
        if (name.Length > MaxNameLength)
            return AppError.Validation($"Access rule name must be at most {MaxNameLength} characters.");
        var description = string.IsNullOrWhiteSpace(command.Description) ? null : command.Description.Trim();
        if (description is { Length: > MaxDescriptionLength })
            return AppError.Validation($"Description must be at most {MaxDescriptionLength} characters.");

        AccessRule? existing = null;
        if (command.Id is { } id) {
            existing = await db.AccessRules.Include(r => r.Clauses).FirstOrDefaultAsync(r => r.Id == id, ct);
            if (existing is null) return AppError.NotFound($"Access rule {id} not found.");
        }

        // Immutable on an update, so an attachment's meaning cannot change underneath it.
        var realmId = existing?.RealmId ?? command.RealmId ?? Realm.SystemRealmId;
        if (existing is null && !await db.Realms.AsNoTracking().AnyAsync(r => r.Id == realmId, ct))
            return AppError.Validation($"No realm exists with id {realmId}.");

        var normalized = name.ToUpperInvariant();
        // Scoped to the realm like the unique index behind it. The index is what settles a race between two
        // administrators; this check exists to make the common case a clear Conflict.
        if (await db.AccessRules.AsNoTracking().AnyAsync(
                r => r.RealmId == realmId && r.NormalizedName == normalized && r.Id != (existing != null ? existing.Id : 0),
                ct)) {
            return AppError.Conflict($"An access rule named '{name}' already exists in that realm.");
        }

        var validated = await ValidateClausesAsync(command.Clauses ?? [], realmId, ct);
        if (validated.Error is { } clauseError) return clauseError;
        var clauses = validated.Clauses;

        if (existing is null) {
            existing = new AccessRule {
                RealmId = realmId,
                Name = name,
                NormalizedName = normalized,
                Description = description,
                CreatedAt = time.GetUtcNow(),
            };
            db.AccessRules.Add(existing);
        } else {
            existing.Name = name;
            existing.NormalizedName = normalized;
            existing.Description = description;
        }

        ReconcileClauses(existing, clauses);
        await db.SaveChangesAsync(ct);

        // A rule's contents decide what the edge admits on every route that names it, so a change here is a
        // change to the projection. Best-effort like the route CRUD handlers: a proxy hiccup must not fail a
        // rule edit that already committed.
        await proxy.ApplyAsync(ct);

        var attachedRouteCount = await db.RouteAccessRules.CountAsync(a => a.AccessRuleId == existing.Id, ct);
        var (inProcess, cloudflare) = AccessRuleMapping.Portability(clauses.Select(c => c.Kind));
        return new Response(new AccessRuleDto(
            existing.Id, existing.Name, existing.RealmId, existing.Description,
            [.. clauses.Select(c => new AccessRuleClauseDto(
                c.Kind, c.UserId, c.GroupId, c.Value,
                AccessRuleMapping.LabelFor(c.Kind, c.UserName, c.GroupName, c.Value)))],
            inProcess, cloudflare, attachedRouteCount));
    }

    /// <summary>One validated clause, with the display names the response needs.</summary>
    private sealed record ValidClause(
        AccessClauseKind Kind, int? UserId, int? GroupId, string? Value, string? UserName, string? GroupName);

    private sealed record ClauseValidation(AppError? Error, List<ValidClause> Clauses);

    /// <summary>
    /// Checks every clause before anything is written: a known kind, exactly the subject column that kind
    /// requires, a subject that exists, and — for the account kinds — one of the rule's own realm.
    /// </summary>
    /// <remarks>
    /// The realm check is the same invariant <see cref="SetAccess"/> applies to grants (design.md §13): a
    /// clause naming a foreign account would admit nobody at either enforcement point, so it is refused here
    /// rather than stored as a row that reads like access somebody has.
    /// </remarks>
    private async Task<ClauseValidation> ValidateClausesAsync(
        IReadOnlyList<AccessRuleClauseDto> requested, int realmId, CancellationToken ct) {
        var clauses = new List<ValidClause>(requested.Count);
        var userIds = new List<int>();
        var groupIds = new List<int>();

        foreach (var clause in requested) {
            if (!Enum.IsDefined(clause.Kind))
                return new ClauseValidation(AppError.Validation($"Unknown access clause kind '{clause.Kind}'."), clauses);

            var kindName = AccessClauseSupport.Describe(clause.Kind);
            switch (clause.Kind) {
                case AccessClauseKind.User:
                    if (clause.UserId is not { } userId)
                        return new ClauseValidation(AppError.Validation($"A {kindName} clause needs a user id."), clauses);
                    if (clause.GroupId is not null || !string.IsNullOrWhiteSpace(clause.Value)) {
                        return new ClauseValidation(
                            AppError.Validation($"A {kindName} clause carries only a user id."), clauses);
                    }
                    userIds.Add(userId);
                    clauses.Add(new ValidClause(clause.Kind, userId, null, null, null, null));
                    break;
                case AccessClauseKind.Group:
                    if (clause.GroupId is not { } groupId)
                        return new ClauseValidation(AppError.Validation($"A {kindName} clause needs a group id."), clauses);
                    if (clause.UserId is not null || !string.IsNullOrWhiteSpace(clause.Value)) {
                        return new ClauseValidation(
                            AppError.Validation($"A {kindName} clause carries only a group id."), clauses);
                    }
                    groupIds.Add(groupId);
                    clauses.Add(new ValidClause(clause.Kind, null, groupId, null, null, null));
                    break;
                default:
                    if (clause.UserId is not null || clause.GroupId is not null) {
                        return new ClauseValidation(
                            AppError.Validation($"A {kindName} clause carries only a value."), clauses);
                    }
                    var value = clause.Value?.Trim();
                    if (string.IsNullOrEmpty(value))
                        return new ClauseValidation(AppError.Validation($"A {kindName} clause needs a value."), clauses);
                    if (value.Length > MaxValueLength) {
                        return new ClauseValidation(
                            AppError.Validation($"A {kindName} value must be at most {MaxValueLength} characters."),
                            clauses);
                    }
                    if (ValidateValue(clause.Kind, value) is { } invalidValue)
                        return new ClauseValidation(invalidValue, clauses);
                    clauses.Add(new ValidClause(clause.Kind, null, null, value, null, null));
                    break;
            }
        }

        // Both existence checks run before any write, so a rule naming one good and one unknown subject is
        // refused whole. Resolved in bulk rather than per clause: a rule can name a dozen subjects.
        var users = userIds.Count == 0
            ? []
            : await db.Users.AsNoTracking()
                .Where(u => userIds.Contains(u.Id))
                .Select(u => new { u.Id, u.RealmId, u.UserName })
                .ToListAsync(ct);
        if (userIds.Count > 0) {
            var missing = userIds.Distinct().Except(users.Select(u => u.Id)).OrderBy(x => x).ToList();
            if (missing.Count > 0)
                return new ClauseValidation(AppError.Validation($"No user exists with id {Describe(missing)}."), clauses);
            var foreign = users.Where(u => u.RealmId != realmId).Select(u => u.Id).OrderBy(x => x).ToList();
            if (foreign.Count > 0) {
                return new ClauseValidation(
                    AppError.Validation($"User {Describe(foreign)} belongs to a different realm than this rule."),
                    clauses);
            }
        }

        var groups = groupIds.Count == 0
            ? []
            : await db.Groups.AsNoTracking()
                .Where(g => groupIds.Contains(g.Id))
                .Select(g => new { g.Id, g.RealmId, g.Name })
                .ToListAsync(ct);
        if (groupIds.Count > 0) {
            var missing = groupIds.Distinct().Except(groups.Select(g => g.Id)).OrderBy(x => x).ToList();
            if (missing.Count > 0)
                return new ClauseValidation(AppError.Validation($"No group exists with id {Describe(missing)}."), clauses);
            var foreign = groups.Where(g => g.RealmId != realmId).Select(g => g.Id).OrderBy(x => x).ToList();
            if (foreign.Count > 0) {
                return new ClauseValidation(
                    AppError.Validation($"Group {Describe(foreign)} belongs to a different realm than this rule."),
                    clauses);
            }
        }

        // The same subject twice is one clause, not two: the partial unique indexes say so, and a duplicate
        // would otherwise fail the insert with a database exception instead of being quietly idempotent.
        var seen = new HashSet<(AccessClauseKind, int?, int?, string?)>();
        var deduped = new List<ValidClause>(clauses.Count);
        foreach (var clause in clauses) {
            var key = (clause.Kind, clause.UserId, clause.GroupId, clause.Value?.ToUpperInvariant());
            if (!seen.Add(key)) continue;
            deduped.Add(clause with {
                UserName = users.FirstOrDefault(u => u.Id == clause.UserId)?.UserName,
                GroupName = groups.FirstOrDefault(g => g.Id == clause.GroupId)?.Name,
            });
        }

        return new ClauseValidation(null, deduped);
    }

    /// <summary>
    /// Shape checks for the value-carrying kinds. Deliberately shallow for the two opaque kinds — they are
    /// somebody else's identifiers and Watchtower is in no position to say what one looks like; a wrong id
    /// surfaces as a Cloudflare error in the audit trail, which is where it belongs.
    /// </summary>
    private static AppError? ValidateValue(AccessClauseKind kind, string value) => kind switch {
        // Not a full RFC validation, which would reject addresses that work: one '@' with something either
        // side is the property the edge actually needs to match on.
        AccessClauseKind.Email when value.Count(c => c == '@') != 1 || value.StartsWith('@') || value.EndsWith('@') =>
            AppError.Validation($"'{value}' is not an email address."),
        AccessClauseKind.EmailDomain when value.Contains('@', StringComparison.Ordinal) =>
            AppError.Validation($"'{value}' is an address, not a domain — drop the local part and the '@'."),
        AccessClauseKind.EmailDomain when !value.Contains('.', StringComparison.Ordinal) =>
            AppError.Validation($"'{value}' is not a domain."),
        _ => null,
    };

    /// <summary>
    /// Brings the rule's clause rows to <paramref name="target"/>, reusing rows rather than replacing them:
    /// the surviving rows keep their ids, so re-saving an unchanged rule writes nothing.
    /// </summary>
    private void ReconcileClauses(AccessRule rule, List<ValidClause> target) {
        var current = rule.Clauses.OrderBy(c => c.Order).ThenBy(c => c.Id).ToList();
        for (var i = 0; i < target.Count; i++) {
            var wanted = target[i];
            if (i < current.Count) {
                var row = current[i];
                row.Kind = wanted.Kind;
                row.UserId = wanted.UserId;
                row.GroupId = wanted.GroupId;
                row.Value = wanted.Value;
                row.Order = i;
            } else {
                rule.Clauses.Add(new AccessRuleClause {
                    AccessRuleId = rule.Id,
                    Kind = wanted.Kind,
                    UserId = wanted.UserId,
                    GroupId = wanted.GroupId,
                    Value = wanted.Value,
                    Order = i,
                });
            }
        }
        foreach (var surplus in current.Skip(target.Count))
            db.AccessRuleClauses.Remove(surplus);
    }

    /// <summary>Renders the ids that could not be resolved for the refusal message.</summary>
    private static string Describe(IReadOnlyList<int> ids) =>
        ids.Count == 1 ? ids[0].ToString(CultureInfo.InvariantCulture) : string.Join(", ", ids);
}
