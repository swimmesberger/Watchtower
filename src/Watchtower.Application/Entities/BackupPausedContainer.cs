namespace Watchtower.Application.Entities;

/// <summary>
/// A container a backup run is about to pause (<see cref="BackupQuiesceMode.Pause"/>), written
/// <em>before</em> the pause and deleted once it is unpaused again. The safety net behind the in-process
/// <c>finally</c>: a Watchtower that dies between the pause and the unpause leaves a frozen stack behind,
/// and on its next start it reads this table, unpauses whatever is still paused and clears it
/// (ADR-0019). A row whose container is not paused (the run died before pausing it, or it was resumed
/// by hand) is simply dropped.
/// </summary>
public sealed class BackupPausedContainer {
    public int Id { get; set; }
    /// <summary>The engine's container id.</summary>
    public required string ContainerId { get; set; }
    /// <summary>The container's name at the time, for the log line.</summary>
    public required string ContainerName { get; set; }
    /// <summary>The stack the run belonged to, for the log line and audit row.</summary>
    public required string StackName { get; set; }
    /// <summary>When the row was written — just before the pause.</summary>
    public DateTimeOffset PausedAt { get; set; }
}
