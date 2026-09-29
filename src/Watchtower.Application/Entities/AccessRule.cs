namespace Watchtower.Application.Entities;

/// <summary>
/// A named, reusable allow-list that routes reference instead of restating who gets in
/// ([ADR-0039](../../../docs/decisions/0039-access-rules-compose.md)). A rule holds
/// <see cref="AccessRuleClause"/> rows and nothing else — like <see cref="Group"/> it carries no decision
/// of its own; what it unlocks is decided per route by the <c>route_access_rules</c> attachments.
/// </summary>
/// <remarks>
/// The indirection is the feature. A route names the rule, never the rule's contents, so adding one person
/// is one edit however many hostnames admit them — and a clause may be swapped for an equivalent one the
/// other enforcement point understands (an <see cref="AccessClauseKind.ExternalPolicy"/> for a
/// <see cref="AccessClauseKind.Group"/>, say) without touching a single route row. That is what makes a
/// Cloudflare-only deployment able to adopt Watchtower's own identity plane later without a route
/// migration.
/// <para>
/// Realm-scoped like a group, and for the same reason: a rule whose clauses name accounts is an allow-list
/// over one population (design.md §13). Uniqueness is per realm on <see cref="NormalizedName"/>, so two
/// populations may each have a <c>family</c> and neither can see the other's.
/// </para>
/// </remarks>
public sealed class AccessRule : IHasXmin {
    public int Id { get; set; }

    /// <inheritdoc cref="Group.RealmId"/>
    public int RealmId { get; set; } = Realm.SystemRealmId;

    /// <inheritdoc cref="RealmId"/>
    public Realm? Realm { get; set; }

    /// <summary>Display name as the administrator typed it. Unique within the realm, case-insensitively.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// Upper-cased <see cref="Name"/>, mirroring <see cref="Group.NormalizedName"/>: uniqueness is enforced
    /// on <c>(realm_id, normalized_name)</c> so names are case-insensitive.
    /// </summary>
    public required string NormalizedName { get; set; }

    /// <summary>
    /// What the rule is for, in the operator's words — shown beside the name in the pickers. Optional: a
    /// rule called <c>family</c> holding two email clauses explains itself.
    /// </summary>
    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Mapped by <c>XminConcurrency.UseXminAsConcurrencyToken</c>; see <see cref="IHasXmin"/> for why
    /// this is a real property rather than an EF shadow one.
    /// </summary>
    public uint Xmin { get; private set; }

    /// <summary>The clauses this rule admits, in <see cref="AccessRuleClause.Order"/> order.</summary>
    public ICollection<AccessRuleClause> Clauses { get; set; } = [];
}
