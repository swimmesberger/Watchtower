namespace Watchtower.Application.Entities;

/// <summary>Records the status and outcome of a single stack backup run (ADR-0016).</summary>
public sealed class BackupEvent {
    public int Id { get; set; }
    public int StackId { get; set; }
    public Stack? Stack { get; set; }
    /// <summary>Who triggered the backup: "manual" or "schedule".</summary>
    public required string TriggeredBy { get; set; }
    /// <summary>"queued", "running", "success", or "failed".</summary>
    public required string Status { get; set; }
    /// <summary>Provider-relative path of the uploaded archive (null until upload, and on failure).</summary>
    public string? RemotePath { get; set; }
    /// <summary>Uploaded archive size in bytes (after compression/encryption); null until known.</summary>
    public long? SizeBytes { get; set; }
    /// <summary>Progress/outcome log of the run, including the error on failure.</summary>
    public string? Output { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
