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

/// <summary>
/// The template-level twin of <see cref="StackBackupServiceOverride"/>: per-service backup settings every
/// tenant of a <see cref="StackTemplate"/> inherits, so a fleet's "never back up the cache service" is
/// configured once instead of once per tenant (design.md §"Backups across tenants").
/// </summary>
/// <remarks>
/// <b>Precedence is per service, not per knob.</b> A stack that has a row for a service replaces the
/// template's row for that service outright; a service the stack says nothing about takes the template's
/// row whole. Per-knob merging is not expressible here — <see cref="StackBackupServiceOverride.Exclude"/>
/// is a plain <c>bool</c>, so "the stack does not override exclude" and "the stack overrides it to false"
/// are the same value — and inventing a fourth state to make it expressible would buy a merge nobody
/// asked for. The compose label still beats both (ADR-0020).
/// </remarks>
public sealed class TemplateBackupServiceOverride {
    public int Id { get; set; }
    public int TemplateId { get; set; }
    public StackTemplate? Template { get; set; }
    /// <inheritdoc cref="StackBackupServiceOverride.Service"/>
    public required string Service { get; set; }
    /// <inheritdoc cref="StackBackupServiceOverride.Exclude"/>
    public bool Exclude { get; set; }
    /// <inheritdoc cref="StackBackupServiceOverride.Stop"/>
    public string? Stop { get; set; }
    /// <inheritdoc cref="StackBackupServiceOverride.Dump"/>
    public string? Dump { get; set; }
}
