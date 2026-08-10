namespace Watchtower.Application.Entities;

/// <summary>
/// Allows one subject — either a single user or a whole <see cref="Entities.Group"/> — through one route
/// whose <see cref="Route.AccessMode"/> is <see cref="AccessMode.Restricted"/>. Absent for
/// <see cref="AccessMode.Public"/> and <see cref="AccessMode.Authenticated"/> routes, where the mode alone
/// decides.
/// </summary>
/// <remarks>
/// Exactly one of <see cref="UserId"/> and <see cref="GroupId"/> is set, enforced by a database CHECK
/// constraint rather than only by the handlers: a row with neither would be a grant that names nobody and
/// one with both would be a grant whose meaning depends on which column a reader consults first. The
/// uniqueness of a grant is likewise per subject kind (two partial unique indexes), so a route may name a
/// user and a group the user belongs to without the two colliding — that is simply access twice over.
/// </remarks>
public sealed class RouteAccessGrant {
    public int Id { get; set; }

    public int RouteId { get; set; }
    public Route? Route { get; set; }

    /// <summary>The granted account, or <see langword="null"/> when this grant names a group instead.</summary>
    public int? UserId { get; set; }
    public User? User { get; set; }

    /// <summary>The granted group, or <see langword="null"/> when this grant names a single user instead.</summary>
    public int? GroupId { get; set; }
    public Group? Group { get; set; }
}
