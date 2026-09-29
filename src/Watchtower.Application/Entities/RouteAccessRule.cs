namespace Watchtower.Application.Entities;

/// <summary>
/// Attaches one <see cref="AccessRule"/> to one <see cref="Route"/> (ADR-0039 decision 1). A route's
/// attachments are its allow-list; a protected route with none falls back to the instance-wide settings,
/// which is what keeps the model a no-op for a deployment that never creates a rule.
/// </summary>
/// <remarks>
/// Only an <see cref="AccessMode.Authenticated"/> route carries attachments.
/// <see cref="AccessMode.Restricted"/> means "exactly the subjects this route grants" and
/// <see cref="AccessMode.Public"/> means "nobody is asked", so an attachment on either would be state that
/// reads like access somebody has while deciding nothing — refused by <c>proxy.setAccess</c> rather than
/// stored (ADR-0039 decision 2).
/// </remarks>
public sealed class RouteAccessRule {
    public int Id { get; set; }

    public int RouteId { get; set; }
    public Route? Route { get; set; }

    public int AccessRuleId { get; set; }
    public AccessRule? AccessRule { get; set; }

    /// <summary>
    /// Position within the route's list, which becomes the order the projected policies are attached in —
    /// precedence at the edge. Watchtower assigns it from the caller's list rather than letting the
    /// provider choose, so a reconcile that changes nothing writes nothing.
    /// </summary>
    public int Order { get; set; }
}
