namespace Watchtower.Application.Entities;

/// <summary>Records the status and captured output of a single stack deployment run.</summary>
public sealed class DeployEvent {
    public int Id { get; set; }
    public int StackId { get; set; }
    public Stack? Stack { get; set; }
    /// <summary>Who triggered the deploy: "manual" or "webhook".</summary>
    public required string TriggeredBy { get; set; }
    /// <summary>"queued", "running", "success", or "failed".</summary>
    public required string Status { get; set; }
    /// <summary>Captured stdout/stderr from the git + docker compose commands.</summary>
    public string? Output { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
