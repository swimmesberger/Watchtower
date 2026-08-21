namespace Watchtower.Application.Entities;

/// <summary>
/// One entry in Watchtower's general audit trail: something Watchtower <em>did</em> — chiefly writes
/// against external control planes — recorded success or failure. The first populated category is
/// <c>proxy.cloudflare</c> (tunnel configuration pushes, DNS upserts, Access app/policy changes);
/// the shape is deliberately category-agnostic so future planes (deploys, settings changes, CI)
/// land in the same table and the same <c>audit.listEvents</c> surface.
/// </summary>
/// <remarks>
/// Deliberately reference-free: rows describe their subject by name (hostname, app, tunnel), so the
/// trail survives the deletion of whatever it mentions — an audit trail that loses its history with
/// its subject would be useless for exactly the question it exists to answer. Retention is bounded
/// by <see cref="Services.AuditLog"/> (newest N kept), not by time. Distinct from
/// <see cref="AuthEvent"/>, which is the access-control plane's trail of what <em>users</em> did.
/// </remarks>
public sealed class AuditEvent {
    public int Id { get; set; }

    /// <summary>
    /// The plane the event belongs to, as a dotted identifier — e.g. <c>proxy.cloudflare</c>.
    /// Listable by prefix, so <c>proxy</c> matches every provider's events.
    /// </summary>
    public required string Category { get; set; }

    /// <summary>
    /// What happened, as a dotted identifier — e.g. <c>tunnel.config.push</c>, <c>dns.create</c>,
    /// <c>access.app.delete</c>.
    /// </summary>
    public required string Action { get; set; }

    /// <summary>What it acted on — a hostname, tunnel name, or Access application name.</summary>
    public required string Target { get; set; }

    /// <summary>Free-form context (rule counts, the CNAME target, policy rule count).</summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Who triggered it, when a user did (their user name); null for background reconciles, which
    /// the UI renders as <c>system</c>.
    /// </summary>
    public string? Actor { get; set; }

    /// <summary>False when the write failed; <see cref="Error"/> then carries the reason.</summary>
    public bool Success { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
