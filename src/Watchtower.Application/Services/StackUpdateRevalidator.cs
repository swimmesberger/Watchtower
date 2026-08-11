using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services;

/// <summary>
/// Keeps the cached "update available" state honest when a stack is updated outside Watchtower.
/// Read handlers (<c>stacks.list</c> / <c>stacks.get</c>) announce the stacks they served with a
/// pending image update; this starts a local, registry-free revalidation
/// (<see cref="StackUpdateService.RevalidateStackAsync"/>) in the background and never makes the
/// caller wait for it. The correction shows up on the UI's next refetch.
/// </summary>
/// <remarks>
/// Debounced per stack: a dashboard that polls every few seconds, or lists twenty stacks at once,
/// must not turn into twenty Docker inspects per poll. The window is deliberately a constant rather
/// than a setting — it trades staleness nobody can perceive for load nobody has to tune.
/// </remarks>
public sealed class StackUpdateRevalidator(
    StackUpdateService stackUpdate,
    TimeProvider time,
    ILogger<StackUpdateRevalidator> logger) {

    /// <summary>Shortest interval between two revalidations of the same stack.</summary>
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastStarted = new();
    private Task _pending = Task.CompletedTask;

    /// <summary>
    /// The revalidation started most recently. Exposed so callers that need the work to have landed —
    /// tests, above all — can await what the read path deliberately forgets.
    /// </summary>
    public Task Pending => Volatile.Read(ref _pending);

    /// <summary>
    /// Requests a background revalidation of the stack's cached update state.
    /// Returns false when one ran within <see cref="DebounceWindow"/> and this request was dropped.
    /// </summary>
    public bool Request(int stackId) {
        if (!TryClaim(stackId)) return false;
        Volatile.Write(ref _pending, RevalidateAsync(stackId));
        return true;
    }

    /// <summary>
    /// Reserves the stack's next revalidation slot, so concurrent readers cannot both start one.
    /// The slot is taken before the work begins, which also caps a slow revalidation to one in flight.
    /// </summary>
    private bool TryClaim(int stackId) {
        var now = time.GetUtcNow();
        while (true) {
            if (_lastStarted.TryGetValue(stackId, out var previous)) {
                if (now - previous < DebounceWindow) return false;
                if (_lastStarted.TryUpdate(stackId, now, previous)) return true;
            } else if (_lastStarted.TryAdd(stackId, now)) {
                return true;
            }
        }
    }

    private async Task RevalidateAsync(int stackId) {
        // Yield first so the requesting handler is never charged for the Docker round trips.
        await Task.Yield();
        try {
            await stackUpdate.RevalidateStackAsync(stackId, CancellationToken.None);
        } catch (Exception ex) {
            // Best effort by design: the state stays as the last full check left it.
            logger.LogDebug(ex, "Local update revalidation failed for stack {StackId}", stackId);
        }
    }
}
