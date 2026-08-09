namespace Watchtower.Application.Entities;

/// <summary>
/// Allows one user through one route whose <see cref="Route.AccessMode"/> is
/// <see cref="AccessMode.Restricted"/>. Absent for <see cref="AccessMode.Public"/> and
/// <see cref="AccessMode.Authenticated"/> routes, where the mode alone decides.
/// </summary>
public sealed class RouteAccessGrant {
    public int Id { get; set; }

    public int RouteId { get; set; }
    public Route? Route { get; set; }

    public int UserId { get; set; }
    public User? User { get; set; }
}
