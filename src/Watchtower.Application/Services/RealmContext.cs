using Watchtower.Application.Entities;

namespace Watchtower.Application.Services;

/// <summary>
/// Which realm's credential space the current unit of work reads and writes (docs/central-auth/design.md
/// §13). Scoped, and the one thing <see cref="WatchtowerUserStore"/> consults to decide which population a
/// user-name lookup may see — which is also what realm-scopes ASP.NET Identity's own duplicate-name check,
/// since that check goes through the store like every other lookup.
/// </summary>
/// <remarks>
/// The default is the system realm, so every code path that never sets it behaves exactly as it did before
/// realms existed. Two kinds of caller set it: the login endpoint, to the realm the request's host resolves
/// to (<see cref="RealmResolver.ResolveByHostAsync"/>), and the administrative user handlers, to the realm
/// of the account they are about to create or edit.
/// </remarks>
public interface IRealmContext {
    /// <summary>The realm in scope. <see cref="Realm.SystemRealmId"/> until something says otherwise.</summary>
    int RealmId { get; }

    /// <summary>
    /// Pins the scope to <paramref name="realmId"/>. Deliberately a method rather than a setter: this is an
    /// ambient security decision, and it should read like one at the call site.
    /// </summary>
    void SetRealm(int realmId);
}

/// <inheritdoc cref="IRealmContext"/>
public sealed class RealmContext : IRealmContext {
    /// <inheritdoc />
    public int RealmId { get; private set; } = Realm.SystemRealmId;

    /// <inheritdoc />
    public void SetRealm(int realmId) {
        ArgumentOutOfRangeException.ThrowIfLessThan(realmId, 1);
        RealmId = realmId;
    }
}
