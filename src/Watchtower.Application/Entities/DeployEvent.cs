namespace Watchtower.Application.Entities;

/// <summary>Records the status and captured output of a single stack deployment run.</summary>
public sealed class DeployEvent {
    public int Id { get; set; }
    public int StackId { get; set; }
    public Stack? Stack { get; set; }
    /// <summary>
    /// Who triggered the deploy: "manual", "webhook", "volume-recreate", "auto-update" (pull-based
    /// on-change), "schedule" (pull-based daily window), "release" (a release arrived and fanned out to
    /// this stack) or "release-manual" (an operator pinned, unpinned or rolled a release out).
    /// </summary>
    /// <remarks>
    /// "release" is the one trigger a deploy may short-circuit on
    /// (<see cref="Services.DeployTriggers.MayShortCircuit"/>): a fan-out is only ever asking a stack to
    /// converge onto the newest release, so a stack already on it has nothing to do. Every other
    /// trigger — manual, webhook, schedule, release-manual — runs the full pipeline even when the
    /// release does not change, because a deploy also converges compose, config and environment.
    /// </remarks>
    public required string TriggeredBy { get; set; }
    /// <summary>"queued", "running", "success", or "failed".</summary>
    public required string Status { get; set; }
    /// <summary>
    /// The release this deploy applied, stamped once it is resolved at execution time — null for a
    /// deploy of a <c>Git</c>-mode product, and for one that failed before resolution.
    /// </summary>
    /// <remarks>
    /// Resolved at execution rather than captured at enqueue (invariant 3, design.md §Convergent
    /// fan-out): a coalesced event therefore reports the release that actually ran, which is the whole
    /// point of the convergent rule. <c>SET NULL</c> — deleting a release must not take the history of
    /// the deploys that ran it.
    /// </remarks>
    public int? ReleaseId { get; set; }
    public Release? Release { get; set; }
    /// <summary>Captured stdout/stderr from the git + docker compose commands.</summary>
    public string? Output { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
