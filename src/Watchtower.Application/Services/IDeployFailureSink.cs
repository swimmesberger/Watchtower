namespace Watchtower.Application.Services;

/// <summary>
/// Where <see cref="DeployQueueService"/> reports a deploy that ended <c>failed</c>, so something else
/// can react to it — today the operators' push notification (ADR-0041).
/// </summary>
/// <remarks>
/// A port owned by the deploy engine rather than a dependency on the Notifications module: the engine
/// says what happened and knows nothing about who listens. The contract is fire-and-forget on purpose —
/// the call sits on the deploy's completion path, so an implementation must return immediately (hand the
/// id to a queue, never do I/O inline) and must never throw; a notification that could not be sent is
/// the listener's problem, not a reason for a deploy's bookkeeping to fail. Only the event id travels,
/// never the output: the listener loads what it needs, and deploy output can carry secrets.
/// </remarks>
public interface IDeployFailureSink {
    /// <summary>Records that the deploy tracked by <paramref name="deployEventId"/> finished as failed.</summary>
    void DeployFailed(int deployEventId);
}
