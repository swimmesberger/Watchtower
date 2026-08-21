namespace Watchtower.Application.Entities;

/// <summary>
/// One entry in the instance's audit trail — the single record of what happened here: what users
/// did (logins, denials, account and policy changes — the access-control plane, categories
/// <c>auth</c>, <c>access</c>, <c>users</c>, <c>groups</c>, <c>realms</c>) and what Watchtower did
/// as a result or on its own (writes against external control planes such as <c>proxy.cloudflare</c>,
/// <c>backups</c> runs, <c>system</c> and <c>metrics</c> settings changes). One shape for all of it,
/// read through the one <c>audit.*</c> surface, so a new plane integrates by recording under a new
/// category and nothing else.
/// </summary>
/// <remarks>
/// Deliberately reference-free: rows describe their subjects by name (account, hostname, app,
/// tunnel), so the trail survives the deletion of whatever it mentions — an audit trail that loses
/// its history with its subject would be useless for exactly the question it exists to answer.
/// Retention is bounded by <see cref="Services.AuditLog"/> (newest N kept per category), not by
/// time. Two ways in: <see cref="Services.AuditLog"/> for best-effort writers without a transaction
/// of their own, and a row added to the caller's own context (<see cref="Services.AuthAudit"/>) for
/// the access-control plane, whose rows must commit with the act they record.
/// </remarks>
public sealed class AuditEvent {
    public int Id { get; set; }

    /// <summary>
    /// The plane the event belongs to, as a dotted identifier — e.g. <c>proxy.cloudflare</c>,
    /// <c>backups</c>, <c>auth</c>. Listable by prefix, so <c>proxy</c> matches every provider's events.
    /// </summary>
    public required string Category { get; set; }

    /// <summary>
    /// What happened, as a dotted identifier — e.g. <c>tunnel.config.push</c>, <c>login.failed</c>,
    /// <c>user.created</c>, <c>config.update</c>.
    /// </summary>
    public required string Action { get; set; }

    /// <summary>What it acted on — an account, a hostname, a tunnel name, a stack, a settings surface.</summary>
    public required string Target { get; set; }

    /// <summary>Free-form context (rule counts, the CNAME target, the changed fields, the remote address).</summary>
    public string? Detail { get; set; }

    /// <summary>
    /// Who did it, when a user did (their account name; <c>local</c> for the implicit local
    /// administrator); null for background work and startup hooks, which the UI renders as
    /// <c>system</c>.
    /// </summary>
    public string? Actor { get; set; }

    /// <summary>False for a failed write, a rejected login or a refused access; <see cref="Error"/> may carry the reason.</summary>
    public bool Success { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
