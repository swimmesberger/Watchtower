using System.Text;
using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services;

/// <summary>
/// The mechanics shared by everything that recreates Watchtower's own container from a sibling
/// container: how such a coordinator is launched, how its exit is waited for, and how its output is
/// collected when it did not exit cleanly.
/// </summary>
/// <remarks>
/// Two callers today — <see cref="SelfUpdateService"/> (recreate onto a new image) and
/// <see cref="SelfPortPublishService"/> (recreate with different host ports). What is deliberately
/// <em>not</em> here is the bookkeeping either of them does around these calls: which settings record the
/// outcome is written to, and what an exit code means for the operator, are the callers' own, and folding
/// them together would give one of the two the other's apply state.
/// </remarks>
internal static class CoordinatorContainers {
    /// <summary>
    /// Creates and starts a coordinator container from <paramref name="image"/> and returns its id.
    /// </summary>
    /// <remarks>
    /// It gets the Docker socket and nothing else. The recreate is a pure Docker API operation, so no
    /// network (<c>none</c>) and no volumes are needed — and a coordinator that cannot reach anything
    /// else is a coordinator that cannot do anything else.
    /// </remarks>
    /// <param name="cmd">The coordinator's own command line, starting with the mode flag.</param>
    /// <param name="dockerApiVersion">
    /// Passed through as an environment variable so the sibling talks to the daemon over the same API
    /// version this process negotiated, rather than falling back to its compiled-in default.
    /// </param>
    public static async Task<string> SpawnAsync(
        DockerEngineClient docker,
        string image,
        string[] cmd,
        string dockerApiVersion,
        string name,
        CancellationToken ct) {
        var id = await docker.CreateContainerAsync(new DockerCreateContainerBody {
            Image = image,
            Cmd = cmd,
            Env = [$"WATCHTOWER__DOCKERAPIVERSION={dockerApiVersion}"],
            HostConfig = new DockerCreateHostConfig {
                Binds = ["/var/run/docker.sock:/var/run/docker.sock"],
                NetworkMode = "none",
                GroupAdd = HostSupplementaryGroups.Current(),
            },
        }, name, ct);
        await docker.StartContainerAsync(id, ct);
        return id;
    }

    /// <summary>
    /// Waits for the coordinator to exit under a ceiling of its own, since the wait itself is
    /// unbounded (the daemon holds the response until the container stops, on the untimed client).
    /// Returns false when the ceiling won — the caller then leaves its apply state untouched for a later
    /// reconcile, exactly as a cancellation would.
    /// </summary>
    public static async Task<bool> TryWaitForExitAsync(
        DockerEngineClient docker,
        ILogger logger,
        string coordinatorId,
        TimeSpan waitTimeout,
        CancellationToken ct) {
        // The ceiling gets its own source so "the ceiling won" is read off it directly rather than
        // inferred from ct, which a shutdown landing in the same instant would falsify.
        using var ceiling = new CancellationTokenSource(waitTimeout);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, ceiling.Token);
        try {
            await docker.WaitContainerAsync(coordinatorId, bounded.Token);
            return true;
        } catch (OperationCanceledException) when (ceiling.IsCancellationRequested) {
            logger.LogWarning(
                "Coordinator {Id} had not exited after {Timeout}; leaving the apply stage to be reconciled later",
                Short(coordinatorId), waitTimeout);
            return false;
        }
    }

    /// <summary>
    /// The coordinator's last lines, for an operator who has to be told why the recreate failed. Never
    /// throws: the logs are the explanation of a failure, not a second thing that can fail.
    /// </summary>
    public static async Task<string> CollectLogsAsync(
        DockerEngineClient docker, string containerId, CancellationToken ct) {
        try {
            var sb = new StringBuilder();
            await foreach (var line in docker.StreamLogsAsync(containerId, tail: 50, follow: false, ct))
                sb.AppendLine(line);
            return sb.ToString();
        } catch {
            return "(logs unavailable)";
        }
    }

    /// <summary>A container id shortened for a log line, tolerating an id shorter than Docker's.</summary>
    public static string Short(string containerId) {
        ArgumentNullException.ThrowIfNull(containerId);
        return containerId.Length >= 12 ? containerId[..12] : containerId;
    }
}
