namespace Watchtower.Application.Entities;

/// <summary>Cached result of a stack image update check. One row per stack (PK is the stack id).</summary>
public sealed class StackUpdateCheck {
    public int StackId { get; set; }
    public Stack? Stack { get; set; }
    /// <summary>True when at least one container image in the stack has a newer version in the registry.</summary>
    public bool HasUpdates { get; set; }
    /// <summary>Image names (with tag) that have a newer version available. Persisted as newline-separated text.</summary>
    public string[] OutdatedImages { get; set; } = [];
    public DateTimeOffset CheckedAt { get; set; }
}
