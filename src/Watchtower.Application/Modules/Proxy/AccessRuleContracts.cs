using Watchtower.Application.Entities;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy;

/// <summary>
/// Which enforcement point the active proxy provider decides at, on the wire — the single value a client
/// needs in order to know which of an <see cref="AccessRuleDto"/>'s two portability flags applies.
/// </summary>
/// <remarks>
/// A plain enum rather than <see cref="AccessEnforcementPoint"/> itself, which is <c>[Flags]</c>: a flags
/// enum serialises as a comma-joined string and generates a useless union in the TypeScript client, and
/// "which point is active" is never a combination.
/// </remarks>
public enum ActiveEnforcementPoint {
    /// <summary>Watchtower's own forward-auth — the <c>yarp</c> and <c>caddy</c> providers.</summary>
    InProcess,

    /// <summary>A Cloudflare Zero Trust Access application.</summary>
    CloudflareAccess,
}

/// <summary>
/// One clause of an <see cref="AccessRule"/> on the wire (ADR-0039 decision 3). Exactly one of
/// <paramref name="UserId"/>, <paramref name="GroupId"/> and <paramref name="Value"/> carries the subject,
/// decided by <paramref name="Kind"/> — the same shape the table's CHECK constraint enforces.
/// </summary>
/// <param name="Kind">
/// <c>user</c>, <c>group</c>, <c>email</c>, <c>emailDomain</c>, <c>externalGroup</c> or
/// <c>externalPolicy</c>.
/// </param>
/// <param name="Value">
/// The literal subject for the three value-carrying kinds: an address, a domain, or the provider's own id.
/// </param>
/// <param name="SubjectLabel">
/// What to show for this clause without a second call — the account's username, the group's name, or the
/// value itself. Read-only; <see cref="Handlers.SetAccessRule"/> ignores it.
/// </param>
public sealed record AccessRuleClauseDto(
    AccessClauseKind Kind,
    int? UserId = null,
    int? GroupId = null,
    string? Value = null,
    string? SubjectLabel = null);

/// <summary>
/// An access rule with its clauses and the enforcement points that can honour all of them
/// (ADR-0039 decision 4).
/// </summary>
/// <param name="EnforceableInProcess">
/// Whether every clause can be honoured by Watchtower's own forward-auth — so whether this rule may be
/// attached to a route under the <c>yarp</c> and <c>caddy</c> providers.
/// </param>
/// <param name="EnforceableByCloudflareAccess">
/// The same question for the Cloudflare provider. A rule can be enforceable at neither point, which is not
/// an error: a rule with a clause kind this build does not know about, or an empty rule, admits nobody
/// anywhere and is still worth showing rather than hiding.
/// </param>
/// <param name="AttachedRouteCount">
/// How many routes name this rule — what makes a delete refusable with a reason and an edit's blast radius
/// visible before it is made.
/// </param>
public sealed record AccessRuleDto(
    int Id,
    string Name,
    int RealmId,
    string? Description,
    IReadOnlyList<AccessRuleClauseDto> Clauses,
    bool EnforceableInProcess,
    bool EnforceableByCloudflareAccess,
    int AttachedRouteCount);

/// <summary>
/// A reusable Access policy that already exists in the operator's Cloudflare account — the roster an
/// <see cref="AccessClauseKind.ExternalPolicy"/> clause is picked from, so the operator chooses
/// <em>friends</em> rather than pasting a UUID.
/// </summary>
/// <param name="Decision">
/// Cloudflare's own decision word for the policy (<c>allow</c>, <c>deny</c>, <c>bypass</c>, …). Surfaced
/// because attaching a <c>deny</c> policy to a route is a decision an operator should make knowingly, and
/// because Watchtower cannot read what is inside it to warn them any other way.
/// </param>
public sealed record ExternalAccessPolicyDto(string Id, string Name, string? Decision);

/// <summary>
/// Projections shared by the access-rule handlers, so the read and the write agree on what a rule looks
/// like.
/// </summary>
internal static class AccessRuleMapping {
    /// <summary>
    /// The clause's display label: the account's username, the group's name, or — for the value-carrying
    /// kinds — the value itself, which is already the only name it has.
    /// </summary>
    public static string? LabelFor(AccessClauseKind kind, string? userName, string? groupName, string? value) =>
        kind switch {
            AccessClauseKind.User => userName,
            AccessClauseKind.Group => groupName,
            _ => value,
        };

    /// <summary>The two portability flags of a rule, from the kinds its clauses use.</summary>
    public static (bool InProcess, bool CloudflareAccess) Portability(IEnumerable<AccessClauseKind> kinds) {
        var supported = AccessClauseSupport.SupportedByAll(kinds);
        return (supported.HasFlag(AccessEnforcementPoint.InProcess),
            supported.HasFlag(AccessEnforcementPoint.CloudflareAccess));
    }

    /// <summary>The wire spelling of the enforcement point the active provider decides at.</summary>
    public static ActiveEnforcementPoint Active(AccessEnforcementPoint point) =>
        point == AccessEnforcementPoint.CloudflareAccess
            ? ActiveEnforcementPoint.CloudflareAccess
            : ActiveEnforcementPoint.InProcess;
}
