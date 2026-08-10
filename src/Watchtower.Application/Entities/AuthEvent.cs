namespace Watchtower.Application.Entities;

/// <summary>
/// Audit trail for the access-control plane: logins, denials and policy changes. Rows outlive the
/// user or route they mention, so both references detach (rather than cascade) on delete.
/// </summary>
public sealed class AuthEvent {
    public int Id { get; set; }

    /// <summary>What happened, as a dotted identifier — e.g. <c>login.ok</c>, <c>login.failed</c>, <c>access.denied</c>.</summary>
    public required string Kind { get; set; }

    /// <summary>The account involved, when known (a failed login for an unknown name has none).</summary>
    public int? UserId { get; set; }
    public User? User { get; set; }

    /// <summary>The app involved, for access decisions and policy changes.</summary>
    public int? RouteId { get; set; }
    public Route? Route { get; set; }

    /// <summary>Free-form context for the event (reason, remote address, changed fields).</summary>
    public string? Detail { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
