using Watchtower.Application.Config;
using Watchtower.Application.Entities;

namespace Watchtower.Application.Services;

/// <summary>
/// Where a route's access decision is actually made. Two points exist today and they are not
/// interchangeable: one is Watchtower's own code with a session and a realm in hand, the other is somebody
/// else's edge with an email address and no notion of either.
/// </summary>
[Flags]
public enum AccessEnforcementPoint {
    /// <summary>No point at all — the value a clause nothing can honour would carry.</summary>
    None = 0,

    /// <summary>
    /// Watchtower's own forward-auth, i.e. <see cref="RouteAccessPolicy"/> under the <c>yarp</c> and
    /// <c>caddy</c> providers.
    /// </summary>
    InProcess = 1,

    /// <summary>A Cloudflare Zero Trust Access application, projected by <c>CloudflareTunnelProvider</c>.</summary>
    CloudflareAccess = 2,

    /// <summary>Both — a clause that means the same thing wherever the route is served.</summary>
    Everywhere = InProcess | CloudflareAccess,
}

/// <summary>
/// Which enforcement points can honour which <see cref="AccessClauseKind"/> (ADR-0039 decision 3), and the
/// refusal message for the ones that cannot.
/// </summary>
/// <remarks>
/// This table is the difference between a shared vocabulary and a lossy one. The access plane already
/// contains two silent divergences of exactly the kind it exists to prevent — <c>Restricted</c> discarding
/// the configured reusable policy ids, and the realm invariant evaporating in the Cloudflare projection
/// (fixed by ADR-0040 decision 3) — and in both the model said one thing, the edge did another, and nothing
/// told the operator which. So portability is declared once, here, and checked when a route's access is
/// <em>written</em>; a reconcile is never where an operator finds out that half of what they asked for was
/// dropped.
/// <para>
/// A rule may still hold clauses the active provider cannot honour: that is how an operator stages a move
/// from one edge to the other, keeping both spellings on the rule while the cutover happens. What is
/// refused is <em>attaching</em> such a rule to a route the active provider would then mis-serve.
/// </para>
/// </remarks>
public static class AccessClauseSupport {
    /// <summary>The enforcement points that can honour <paramref name="kind"/>.</summary>
    public static AccessEnforcementPoint SupportedBy(AccessClauseKind kind) => kind switch {
        // Subjects Watchtower owns: it can evaluate them against a session, and resolve them to the email
        // addresses the edge matches on.
        AccessClauseKind.User => AccessEnforcementPoint.Everywhere,
        AccessClauseKind.Group => AccessEnforcementPoint.Everywhere,
        // Edge-only because the edge verifies the address and Watchtower does not — see AccessClauseKind.
        AccessClauseKind.Email => AccessEnforcementPoint.CloudflareAccess,
        AccessClauseKind.EmailDomain => AccessEnforcementPoint.CloudflareAccess,
        // Opaque: ids for subject sets and allow-lists the provider owns, so there is nothing to evaluate.
        AccessClauseKind.ExternalGroup => AccessEnforcementPoint.CloudflareAccess,
        AccessClauseKind.ExternalPolicy => AccessEnforcementPoint.CloudflareAccess,
        // A kind this build does not know about is not a licence to let anyone through.
        _ => AccessEnforcementPoint.None,
    };

    /// <summary>Whether <paramref name="point"/> can honour <paramref name="kind"/>.</summary>
    public static bool IsSupportedAt(AccessClauseKind kind, AccessEnforcementPoint point) =>
        (SupportedBy(kind) & point) == point && point != AccessEnforcementPoint.None;

    /// <summary>
    /// The enforcement points that can honour <em>every</em> clause in <paramref name="kinds"/> — the
    /// intersection, because a rule admits the union of its clauses and an edge that drops one of them
    /// admits fewer people than the rule says.
    /// </summary>
    /// <remarks>
    /// An empty sequence is <see cref="AccessEnforcementPoint.Everywhere"/>: a rule with no clauses admits
    /// nobody anywhere, which every point can represent faithfully. Whether a route may <em>attach</em>
    /// such a rule is the lockout question (ADR-0039 decision 5), not a portability one.
    /// </remarks>
    public static AccessEnforcementPoint SupportedByAll(IEnumerable<AccessClauseKind> kinds) {
        ArgumentNullException.ThrowIfNull(kinds);
        var supported = AccessEnforcementPoint.Everywhere;
        foreach (var kind in kinds) supported &= SupportedBy(kind);
        return supported;
    }

    /// <summary>The enforcement point a proxy provider decides a route's access at.</summary>
    /// <remarks>
    /// <c>caddy</c> and <c>yarp</c> both emit a forward-auth hop to Watchtower, so both decide in process;
    /// only the Cloudflare provider hands the decision to somebody else's edge (ADR-0015).
    /// </remarks>
    public static AccessEnforcementPoint PointFor(ProxyProviderKind provider) => provider switch {
        ProxyProviderKind.Cloudflare => AccessEnforcementPoint.CloudflareAccess,
        _ => AccessEnforcementPoint.InProcess,
    };

    /// <summary>
    /// How a clause kind is named in a refusal or a warning — the operator-facing spelling, which is the
    /// label the UI uses too.
    /// </summary>
    public static string Describe(AccessClauseKind kind) => kind switch {
        AccessClauseKind.User => "user",
        AccessClauseKind.Group => "group",
        AccessClauseKind.Email => "email",
        AccessClauseKind.EmailDomain => "email domain",
        AccessClauseKind.ExternalGroup => "external group",
        AccessClauseKind.ExternalPolicy => "external policy",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Several clause kinds named as one phrase, pluralised — <c>"email and email domain clauses"</c>.
    /// </summary>
    public static string DescribeAll(IReadOnlyCollection<AccessClauseKind> kinds) {
        ArgumentNullException.ThrowIfNull(kinds);
        var names = kinds.Select(Describe).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        return names.Length switch {
            0 => "no clauses",
            1 => $"{names[0]} clauses",
            _ => $"{string.Join(", ", names[..^1])} and {names[^1]} clauses",
        };
    }

    /// <summary>How an enforcement point is named in a refusal — what an operator would call it.</summary>
    public static string Describe(AccessEnforcementPoint point) => point switch {
        AccessEnforcementPoint.CloudflareAccess => "Cloudflare Access",
        AccessEnforcementPoint.InProcess => "Watchtower's own forward-auth",
        AccessEnforcementPoint.Everywhere => "every provider",
        _ => "no provider",
    };
}
