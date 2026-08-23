using Elarion.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// How one instance tells the others that the proxy plane changed — ADR-0024 decision 6. A route,
/// realm or certificate write bumps <see cref="WatchtowerSettingPaths.ProxyRoutesVersion"/>; every
/// instance watches that key through the Elarion settings store's PostgreSQL <c>LISTEN/NOTIFY</c>
/// change source (<c>Elarion.Settings.PostgreSql</c>) and re-projects what it derives from the database.
/// </summary>
/// <remarks>
/// <para>
/// The value is a fresh random string, not a counter. A counter would need a read-modify-write with
/// <c>expectedVersion</c> and a retry loop for the case two instances bump at once, to produce a number
/// nobody reads: the only thing a watcher does with the value is notice that it is different. Writing an
/// unconditional random value makes concurrent bumps a non-event.
/// </para>
/// <para>
/// This is a <em>second</em> path, not a replacement for the direct <c>ApplyAsync</c> the write handlers
/// already do. The local call is what makes the instance an operator is talking to correct before it
/// answers them; the signal is what makes the others correct a moment later. Dropping the local call
/// would put a database round trip and a notification hop between "the route was saved" and "the route
/// is served" on the one instance that could have known immediately.
/// </para>
/// <para>
/// Watchers debounce. A single operator action can produce several writes (a route create that also
/// touches a realm, a reconcile that installs three certificates), and each one costs a full
/// re-projection; coalescing a burst into one pass is the difference between a re-read and a re-read per
/// row.
/// </para>
/// </remarks>
public sealed class ProxyChangeSignal(
    IServiceScopeFactory scopeFactory, ILogger<ProxyChangeSignal> logger) {
    /// <summary>How long a watcher waits for the burst to finish before it re-projects.</summary>
    public static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Announces that the proxy plane changed. Never throws: a signal that could fail a route create
    /// would trade a correct write on this instance for a convergence delay on the others, which is the
    /// wrong way round — the next write, or the next five-minute reconcile, carries the news anyway.
    /// </summary>
    /// <param name="reason">What changed, for the log. Not stored.</param>
    public async Task BumpAsync(string reason, CancellationToken ct = default) {
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
            await settings.SetStringAsync(
                WatchtowerSettingPaths.ProxyRoutesVersion,
                // Version 7 rather than 4: the values end up ordered in an index, and a monotonic prefix
                // keeps that index from fragmenting for no reason.
                Guid.CreateVersion7().ToString("N"),
                SettingsScope.Global,
                expectedVersion: null,
                ct);
            logger.LogDebug("Signalled a proxy change to the other instances ({Reason}).", reason);
        } catch (Exception ex) {
            logger.LogWarning(
                ex, "Could not signal the proxy change '{Reason}' to the other instances.", reason);
        }
    }

    /// <summary>
    /// Runs <paramref name="onChanged"/> whenever any instance bumps the version, coalescing bursts
    /// within <paramref name="debounce"/>. Dispose the result to stop watching.
    /// </summary>
    /// <remarks>
    /// The change token comes from the singleton change source even though
    /// <see cref="ISettingsManager"/> is scoped, so the scope opened to ask for it can close immediately
    /// — the token outlives it. Going through the manager rather than the source directly keeps every
    /// caller on one settings API, including the scope-resolution rule that comes with it.
    /// </remarks>
    public IDisposable Watch(Func<CancellationToken, Task> onChanged, TimeSpan? debounce = null) {
        ArgumentNullException.ThrowIfNull(onChanged);
        return new Subscription(this, onChanged, debounce ?? DefaultDebounce, logger);
    }

    /// <summary>Whether a watcher's pass is in flight. For tests, and for nothing else.</summary>
    internal static bool IsRunning(IDisposable watch) => ((Subscription)watch).IsRunning;

    private IChangeToken WatchToken() {
        using var scope = scopeFactory.CreateScope();
        return scope.ServiceProvider.GetRequiredService<ISettingsManager>()
            .Watch(WatchtowerSettingPaths.ProxyRoutesVersion, SettingsScope.Global);
    }

    /// <summary>
    /// One watcher: a re-registering change-token subscription in front of a debounced, strictly
    /// non-overlapping run of the callback.
    /// </summary>
    /// <remarks>
    /// The two properties worth stating, because they pull in opposite directions and a naive
    /// implementation gets one of them wrong. <b>At most one pass runs at a time</b> — the callback
    /// re-projects a route table and rebuilds certificate contexts, and two of those interleaving would
    /// have them racing over the same maps. <b>At least one pass runs after the last signal</b> — a
    /// change that arrives while a pass is already running must not be swallowed by it, because that
    /// pass may have read the database before the change landed. A single loop with a dirty flag gives
    /// both: the flag is cleared just before the callback is invoked, so anything signalled during the
    /// debounce is covered by the pass about to run and anything signalled during the pass earns
    /// another one.
    /// </remarks>
    private sealed class Subscription : IDisposable {
        private readonly Func<CancellationToken, Task> _onChanged;
        private readonly TimeSpan _debounce;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _stopping = new();
        private readonly IDisposable _registration;
        private readonly Lock _gate = new();
        private Task _pending = Task.CompletedTask;

        /// <summary>Something changed and no pass has covered it yet.</summary>
        private bool _dirty;

        /// <summary>A pass is in flight (waiting out the debounce, or running the callback).</summary>
        private bool _running;

        private bool _disposed;

        public Subscription(
            ProxyChangeSignal signal, Func<CancellationToken, Task> onChanged, TimeSpan debounce,
            ILogger logger) {
            _onChanged = onChanged;
            _debounce = debounce;
            _logger = logger;
            // OnChange re-registers after every fire, which is what the one-shot token contract requires.
            _registration = ChangeToken.OnChange(signal.WatchToken, Schedule);
        }

        /// <summary>Whether a pass is in flight. For tests, and for nothing else.</summary>
        internal bool IsRunning {
            get { lock (_gate) return _running; }
        }

        private void Schedule() {
            lock (_gate) {
                if (_disposed) return;
                _dirty = true;
                // A pass is already in flight; it re-checks the flag when it finishes rather than being
                // joined by a second one.
                if (_running) return;
                _running = true;
                _pending = RunAsync();
            }
        }

        private async Task RunAsync() {
            try {
                while (true) {
                    try {
                        await Task.Delay(_debounce, _stopping.Token);
                    } catch (OperationCanceledException) {
                        return;
                    }

                    lock (_gate) {
                        if (_disposed) return;
                        // Cleared here rather than when the pass was scheduled: everything signalled up
                        // to this instant is covered by the callback about to run, and everything
                        // signalled after it sets the flag again and earns another pass.
                        _dirty = false;
                    }

                    try {
                        await _onChanged(_stopping.Token);
                    } catch (OperationCanceledException) when (_stopping.IsCancellationRequested) {
                        return;
                    } catch (Exception ex) {
                        // The watcher is the only thing keeping this instance converged; it does not get
                        // to die over one failed pass. The next signal — or the reconcile loop — retries.
                        _logger.LogWarning(ex, "Re-projecting after a proxy change signal failed.");
                    }

                    lock (_gate) {
                        if (_disposed || !_dirty) return;
                    }
                }
            } finally {
                // On every exit, including the cancelled and the unexpected ones: a loop that stopped
                // without clearing this would leave the watcher permanently deaf.
                lock (_gate) _running = false;
            }
        }

        public void Dispose() {
            lock (_gate) {
                if (_disposed) return;
                _disposed = true;
            }
            _registration.Dispose();
            _stopping.Cancel();
            try {
                _pending.Wait(TimeSpan.FromSeconds(5));
            } catch (Exception) {
                // Shutdown; whatever the pass was doing is not worth failing the stop over.
            }
            _stopping.Dispose();
        }
    }
}
