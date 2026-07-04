using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Watchtower.Application.Services;

/// <summary>
/// Singleton service that fans out deploy output lines to SSE subscribers in real time.
/// Each active deploy event gets a <see cref="DeploySession"/> that buffers historical lines
/// and broadcasts new ones to any currently-connected subscribers.
/// </summary>
public sealed class DeployOutputBroadcaster {
    private readonly ConcurrentDictionary<int, DeploySession> _sessions = new();

    /// <summary>
    /// Creates a fresh session for <paramref name="eventId"/>.
    /// Must be called once before any <see cref="DeploySession.Write"/> calls.
    /// </summary>
    public DeploySession Create(int eventId) {
        var session = new DeploySession();
        _sessions[eventId] = session;
        return session;
    }

    /// <summary>Writes a line to the session for <paramref name="eventId"/> (no-op if session is gone).</summary>
    public void Write(int eventId, string line) {
        if (_sessions.TryGetValue(eventId, out var session))
            session.Write(line);
    }

    /// <summary>
    /// Completes and removes the session for <paramref name="eventId"/>.
    /// All subscriber channels are completed so their read loops terminate.
    /// </summary>
    public void Complete(int eventId) {
        if (_sessions.TryRemove(eventId, out var session))
            session.Complete();
    }

    /// <summary>
    /// Returns the active session for <paramref name="eventId"/>, or <c>null</c> when the
    /// deploy has already finished (session removed on completion).
    /// </summary>
    public DeploySession? TryGet(int eventId) =>
        _sessions.TryGetValue(eventId, out var s) ? s : null;
}

/// <summary>
/// Holds the in-progress output for one deploy event.
/// Thread-safe: all mutations and subscriptions hold an internal lock.
/// </summary>
public sealed class DeploySession {
    private readonly object _lock = new();
    private readonly List<string> _history = [];
    private readonly List<Channel<string>> _subscribers = [];
    private bool _completed;

    /// <summary>
    /// Appends <paramref name="line"/> to history and fans it out to all live subscribers.
    /// No-op after <see cref="Complete"/> has been called.
    /// </summary>
    public void Write(string line) {
        lock (_lock) {
            if (_completed) return;
            _history.Add(line);
            foreach (var ch in _subscribers)
                ch.Writer.TryWrite(line);
        }
    }

    /// <summary>Marks the session as complete and closes all subscriber channels.</summary>
    public void Complete() {
        lock (_lock) {
            _completed = true;
            foreach (var ch in _subscribers)
                ch.Writer.TryComplete();
            _subscribers.Clear();
        }
    }

    /// <summary>
    /// Subscribes to the session.
    /// Returns the history collected so far and, when the session is still active,
    /// a <see cref="ChannelReader{T}"/> that will yield all future lines.
    /// When the session is already completed the reader is <c>null</c>.
    /// The snapshot and channel registration are performed atomically under the lock
    /// to prevent any line from being missed or duplicated.
    /// </summary>
    public (IReadOnlyList<string> History, ChannelReader<string>? Live) Subscribe() {
        lock (_lock) {
            if (_completed)
                return (_history.ToArray(), null);

            var ch = Channel.CreateUnbounded<string>(new UnboundedChannelOptions {
                SingleReader = true,
                SingleWriter = false,
            });
            _subscribers.Add(ch);
            return (_history.ToArray(), ch.Reader);
        }
    }
}
