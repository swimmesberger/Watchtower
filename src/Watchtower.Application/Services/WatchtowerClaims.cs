using System.Security.Claims;

namespace Watchtower.Application.Services;

/// <summary>
/// The claim vocabulary shared by the two ends of Watchtower's native login: the authentication handler
/// that mints the principal (<c>Watchtower.Api</c>) and the claims-to-<c>ICurrentUser</c> mapping the
/// application layer configures. Both sides read these constants so the mapping cannot silently drift —
/// a mismatch would not fail the build, it would quietly produce an authenticated user with no id or role.
/// </summary>
public static class WatchtowerClaims {
    /// <summary>Claim carrying <see cref="Entities.User.Id"/> — what <c>ICurrentUser.UserId</c> resolves to.</summary>
    public const string UserId = ClaimTypes.NameIdentifier;

    /// <summary>Claim carrying <see cref="Entities.User.UserName"/> — the display name.</summary>
    public const string Name = ClaimTypes.Name;

    /// <summary>Claim carrying <see cref="Entities.User.Email"/>, when the account has one.</summary>
    public const string Email = ClaimTypes.Email;

    /// <summary>Claim type roles are emitted under.</summary>
    public const string Role = ClaimTypes.Role;

    /// <summary>The single role in v1: user management and system configuration (<see cref="Entities.User.IsAdmin"/>).</summary>
    public const string AdminRole = "Admin";
}
