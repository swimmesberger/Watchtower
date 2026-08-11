using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services;

/// <summary>
/// Keeps the cached "update available" state honest when a stack is updated outside Watchtower.
/// Read handlers (<c>stacks.list</c> / <c>stacks.get</c>) announce the stacks they served with a
/// pending image update; this runs a local, registry-free revalidation
/// (<see cref="StackUpdateService.RevalidateStackAsync"/>) in the background and never makes the
/// caller wait for it. The correction shows up on the UI's next refetch.
/// </summary>
/// <remarks>
/// Two limits, because the read path can announce a lot at once. Requests are <em>debounced</em> per
/// stack — a dashboard polling every few seconds, or listing twenty stacks, must not re-inspect the
/// same stack every time; the window is deliberately a constant rather than a setting, trading
/// staleness nobody can perceive for load nobody has to tune. And the work itself is <em>serialized</em>
/// onto a single chain, so those requests queue instead of hitting a small host all at once.
/// <para>
/// Serializing caps the concurrency, not the total: each pass still costs one container listing plus
/// one inspect per outdated image of that stack, so twenty outdated stacks are twenty listings spread
/// over the queue. That is affordable at the scale Watchtower runs at and bounded by the debounce
/// window; a shared listing per drain would be the next lever if it ever stops being.
/// </para>
/// </remarks>
public sealed class StackUpdateRevalidator(
    StackUpdateService stackUpdate,
    TimeProvider time,
    ILogger<StackUpdateRevalidator> logger) {

    /// <summary>Shortest interval between two revalidations of the same stack.</summary>
    public static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<int, DateTimeOffset> _lastStarted = new();
    private readonly ConcurrentDictionary<int, byte> _inFlight = new();
    private readonly Lock _gate = new();
    private Task _queue = Task.CompletedTask;

    /// <summary>
    /// Completes once everything requested so far has run. The read path deliberately forgets this
    /// work; tests are the callers that need it to have landed.
    /// </summary>
    internal Task Drained {
        get { lock (_gate) return _queue; }
    }

    /// <summary>
    /// Queues a background revalidation of the stack's cached update state. Returns false when the
    /// request was dropped: one ran within <see cref="DebounceWindow"/>, or one is still in flight.
    /// </summary>
    public bool Request(int stackId) {
        if (!TryClaim(stackId)) return false;
        // One chain, one revalidation at a time — appending to it is the whole queue.
        lock (_gate) _queue = _queue.ContinueWith(
            _ => RevalidateAsync(stackId),
            CancellationToken.None,
            TaskContinuationOptions.RunContinuationsAsynchronously,
            TaskScheduler.Default).Unwrap();
        return true;
    }

    /// <summary>
    /// Reserves the stack's next revalidation slot. The debounce window bounds how often a stack is
    /// looked at; the in-flight marker — released in <see cref="RevalidateAsync"/>'s finally — is what
    /// keeps a revalidation slower than that window from being queued a second time behind itself.
    /// </summary>
    private bool TryClaim(int stackId) {
        if (!_inFlight.TryAdd(stackId, 0)) return false;
        var now = time.GetUtcNow();
        while (true) {
            if (_lastStarted.TryGetValue(stackId, out var previous)) {
                if (now - previous < DebounceWindow) {
                    _inFlight.TryRemove(stackId, out _);
                    return false;
                }
                if (_lastStarted.TryUpdate(stackId, now, previous)) return true;
            } else if (_lastStarted.TryAdd(stackId, now)) {
                return true;
            }
        }
    }

    private async Task RevalidateAsync(int stackId) {
        try {
            await stackUpdate.RevalidateStackAsync(stackId, CancellationToken.None);
        } catch (Exception ex) {
            // Best effort by design: the state stays as the last full check left it.
            logger.LogDebug(ex, "Local update revalidation failed for stack {StackId}", stackId);
        } finally {
            _inFlight.TryRemove(stackId, out _);
        }
    }
}
