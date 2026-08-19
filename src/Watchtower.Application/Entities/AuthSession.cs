namespace Watchtower.Application.Entities;

/// <summary>Which cookie a session backs — the central login, or one proxied app's domain.</summary>
public enum SessionKind {
    /// <summary>The central single-sign-on session on the auth host (<c>__wt_sso</c>). <see cref="AuthSession.RouteId"/> is null.</summary>
    Sso,
    /// <summary>A per-app session on the app's own domain (<c>__wt_access</c>), scoped to <see cref="AuthSession.RouteId"/>.</summary>
    App,
    /// <summary>
    /// A half-finished login: the password was accepted, the second factor was not supplied yet. Its token
    /// is returned in the response body, never as a cookie, and it authenticates <em>nothing</em> — it only
    /// says which account <c>POST /api/auth/login/mfa</c> may finish signing in, once, within five minutes.
    /// </summary>
    /// <remarks>
    /// Sharing the <c>auth_sessions</c> table is what makes the expiry sweep, the hashing at rest and the
    /// cascade on account delete apply to it for free. It is kept out of every authentication path by kind:
    /// <see cref="Services.AuthSessionService.ValidateAsync"/> and
    /// <see cref="Services.AuthSessionService.ValidateAppSessionAsync"/> match on <see cref="Sso"/>/
    /// <see cref="App"/>, and <see cref="Services.AuthSessionService.ValidateAnyAsync"/> excludes this kind
    /// explicitly — a pending record presented as a cookie is simply not a session.
    /// </remarks>
    MfaPending,
}

/// <summary>
/// A server-side, revocable session. Cookies carry a random token; only its hash is stored here, so
/// a database read can neither reconstruct nor replay the cookie. Deleting the row signs the session out.
/// </summary>
public sealed class AuthSession {
    public int Id { get; set; }

    /// <summary>Hash of the random cookie value — the lookup key. Unique.</summary>
    public required string TokenHash { get; set; }

    /// <summary>The authenticated account.</summary>
    public int UserId { get; set; }
    public User? User { get; set; }

    public SessionKind Kind { get; set; }

    /// <summary>The app this session grants access to for <see cref="SessionKind.App"/>; null for SSO sessions.</summary>
    public int? RouteId { get; set; }
    public Route? Route { get; set; }

    /// <summary>Sliding expiry; the absolute cap is derived from <see cref="CreatedAt"/>.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
