using Elarion.Abstractions.Identity;

namespace Watchtower.Api;

/// <summary>
/// The fixed anonymous <see cref="ICurrentUser"/> for Watchtower's no-auth deployment (authentication is
/// the reverse proxy's job — see README). The <c>elarion.session</c> bootstrap composes this into its
/// snapshot: <c>isAuthenticated=false</c>, no roles or grants — deliberately not the claims-based
/// middleware implementation, whose <c>UserId</c> requires an authenticated principal's id claim.
/// </summary>
public sealed class AnonymousCurrentUser : ICurrentUser {
    public string UserId => "anonymous";
    public string? Email => null;
    public IReadOnlyList<string> Roles => [];
    public bool IsAuthenticated => false;
    public bool IsInRole(string role) => false;
}
