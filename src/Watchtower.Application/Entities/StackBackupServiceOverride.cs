namespace Watchtower.Application.Entities;

/// <summary>
/// Per-service backup settings configured in the UI for one compose service of a stack (ADR-0020) —
/// the same three knobs as the <c>watchtower.backup.*</c> compose labels, stored in the labels' own
/// value syntax. A label on the deployed service always wins; these fill in where the label is absent,
/// and the Backups tab renders them as compose labels to paste ("promote to code"). Keyed by service
/// name, so the row survives redeploys and applies to every replica.
/// </summary>
public sealed class StackBackupServiceOverride {
    public int Id { get; set; }
    public int StackId { get; set; }
    public Stack? Stack { get; set; }
    /// <summary>The compose service name (<c>com.docker.compose.service</c>).</summary>
    public required string Service { get; set; }
    /// <summary>Stands in for <c>watchtower.backup.exclude=true</c>.</summary>
    public bool Exclude { get; set; }
    /// <summary><c>"true"</c>, <c>"false"</c> or <c>"pause"</c> — stands in for <c>watchtower.backup.stop</c>; null = not set.</summary>
    public string? Stop { get; set; }
    /// <summary><c>"false"</c> or <c>"postgres"</c> — stands in for <c>watchtower.backup.dump</c>; null = not set.</summary>
    public string? Dump { get; set; }
}
