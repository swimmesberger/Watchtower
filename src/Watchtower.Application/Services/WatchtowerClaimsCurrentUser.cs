using Elarion.Abstractions.Identity;
using Elarion.Identity;

namespace Watchtower.Application.Services;

/// <summary>
/// The <see cref="ICurrentUser"/> used when <c>Auth:Enabled</c> is true: Elarion's claims-backed snapshot,
/// with one correction — an unauthenticated caller reports an empty <see cref="UserId"/> instead of throwing.
/// </summary>
/// <remarks>
/// <see cref="ClaimsPrincipalCurrentUser.UserId"/> throws when the id claim is absent, which is the honest
/// answer for a non-nullable contract and harmless for authorization (it consults
/// <see cref="IsAuthenticated"/> first). The <c>elarion.session</c> bootstrap, however, reads
/// <c>UserId</c> unconditionally — and it is exactly the call an <em>anonymous</em> browser makes before it
/// can reach the login page. Without this shim that request fails with a 500 and the SPA boots blind.
/// </remarks>
public sealed class WatchtowerClaimsCurrentUser(ClaimsPrincipalCurrentUser inner) : ICurrentUser {
    /// <inheritdoc />
    public string UserId => inner.IsAuthenticated ? inner.UserId : string.Empty;

    /// <inheritdoc />
    public string? Email => inner.Email;

    /// <inheritdoc />
    public IReadOnlyList<string> Roles => inner.Roles;

    /// <inheritdoc />
    public bool IsAuthenticated => inner.IsAuthenticated;

    /// <inheritdoc />
    public bool IsInRole(string role) => inner.IsInRole(role);

    /// <inheritdoc />
    public bool HasClaim(string type, string value) => inner.HasClaim(type, value);

    /// <inheritdoc />
    public IEnumerable<string> GetClaimValues(string type) => inner.GetClaimValues(type);
}
