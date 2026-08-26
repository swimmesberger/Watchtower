namespace Watchtower.Application.Entities;

/// <summary>
/// A named set of accounts that can be granted access to a route as one subject
/// (docs/central-auth/design.md §3). Membership is the only thing a group carries — it holds no policy
/// of its own, so deleting one revokes exactly the grants that named it and nothing else.
/// </summary>
/// <remarks>
/// Group names leave the instance: they are forwarded to protected upstreams in the comma-joined
/// <c>Remote-Groups</c>/<c>X-Auth-Request-Groups</c> header and as the JWT's <c>groups</c> claim, and a
/// group-aware application maps them straight onto its own roles. The name is therefore constrained at
/// the source (printable ASCII, no comma — see the Groups module handlers) rather than being escaped at
/// each forwarding site, so no writer can produce a name that a reader would split into two.
/// </remarks>
public sealed class Group : IHasXmin {
    public int Id { get; set; }

    /// <summary>
    /// The population this group belongs to (design.md §13). A group may only ever hold accounts of its own
    /// realm, which is what keeps a group grant from reaching across populations. Defaults to the system
    /// realm, like <see cref="User.RealmId"/>.
    /// </summary>
    public int RealmId { get; set; } = Realm.SystemRealmId;

    /// <inheritdoc cref="RealmId"/>
    public Realm? Realm { get; set; }

    /// <summary>
    /// Display name as the administrator typed it. Unique <em>within the realm</em>, case-insensitively via
    /// <see cref="NormalizedName"/>.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Upper-cased form of <see cref="Name"/>, mirroring the <see cref="User.NormalizedUserName"/>
    /// precedent: uniqueness is enforced on <c>(realm_id, normalized_name)</c> so names are
    /// case-insensitive.
    /// </summary>
    public required string NormalizedName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Mapped by <c>XminConcurrency.UseXminAsConcurrencyToken</c>; see <see cref="IHasXmin"/> for why
    /// this is a real property rather than an EF shadow property. Last, because it is the database's
    /// bookkeeping rather than part of what this entity means.
    /// </remarks>
    public uint Xmin { get; private set; }
}
