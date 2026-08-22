namespace Watchtower.Application.Entities;

/// <summary>
/// How a backup run quiesces a container that writes to a volume being archived (ADR-0019).
/// </summary>
public enum BackupQuiesceMode {
    /// <summary>
    /// <c>docker stop</c>: SIGTERM, the process flushes and exits, the snapshot is application-consistent;
    /// the container is restarted afterwards (cold start). The default.
    /// </summary>
    Stop,

    /// <summary>
    /// <c>docker pause</c>: the cgroup freezer suspends the processes in milliseconds and resumes them
    /// after the snapshot — no SIGTERM wait, no restart, connections survive. The snapshot is only
    /// <em>crash-consistent</em>: whatever the application still held in userspace buffers is not in it.
    /// </summary>
    Pause,
}
