namespace Watchtower.Application.Entities;

/// <summary>
/// A one-time code handing a central login over to a protected app on a different domain, where the
/// central cookie is not sent. The login endpoint mints one and redirects to the app's callback,
/// which redeems it (single use — the row is deleted) and mints the app session.
/// </summary>
public sealed class LoginCode {
    public int Id { get; set; }

    /// <summary>Hash of the random code carried in the redirect URL — the lookup key. Unique.</summary>
    public required string CodeHash { get; set; }

    /// <summary>The account the redeemed code authenticates as.</summary>
    public int UserId { get; set; }
    public User? User { get; set; }

    /// <summary>The app the code is bound to; a code is not redeemable on any other route.</summary>
    public int RouteId { get; set; }
    public Route? Route { get; set; }

    /// <summary>Where the callback sends the browser after redemption. Validated against the route's domain (open-redirect guard).</summary>
    public required string RedirectUri { get; set; }

    /// <summary>Short-lived (about a minute); expired rows are swept rather than redeemed.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
