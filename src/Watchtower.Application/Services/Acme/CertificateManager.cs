using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application.Services.Acme;

/// <summary>
/// What the manager knows about one host, as the API surfaces it.
/// </summary>
/// <param name="Desired">
/// Whether anything currently routes to this host. False marks a leftover: a certificate still on disk
/// for a route that has been deleted or a provider that is no longer active. Reported rather than hidden,
/// because "why is this certificate still here" is exactly the question the list exists to answer.
/// </param>
/// <param name="State">One of <c>none</c>, <c>pending</c>, <c>active</c>, <c>awaitingDns</c>, <c>error</c>.</param>
public sealed record HostCertificateState(
    string Host,
    bool Desired,
    string State,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    string? Issuer,
    DateTimeOffset? LastAttemptAt,
    string? LastError,
    DateTimeOffset? NextAttemptAt,
    int ConsecutiveFailures);

/// <summary>
/// Keeps every host the in-process proxy serves in possession of a valid certificate — ADR-0017. The
/// <see cref="IProxyCertificateManager"/> the provider talks to, and the background loop that does the
/// work.
/// </summary>
/// <remarks>
/// The division of labour is deliberate and is what makes any of this testable: <see cref="CertificateIssuer"/>
/// knows the protocol and nothing about time, <see cref="CertificateRenewalPolicy"/> is pure arithmetic
/// over a clock, and this class holds the state — which hosts are wanted, what happened last time, when
/// to try again — and does the scheduling.
/// <para>
/// A plain <see cref="BackgroundService"/> rather than a scheduled job: the loop is not on a wall-clock
/// cadence but on a "something changed" one. A route added at 14:03 should get its certificate at 14:03,
/// so the loop waits on a signal with a five-minute ceiling rather than on a cron expression.
/// </para>
/// <para>
/// Everything is gated on the provider actually being the in-process one, and on the HTTPS listener
/// actually having bound. Both are runtime-switchable, so the loop stays alive while inactive instead of
/// exiting — a provider switched on from the Settings page has to start issuing without a restart.
/// </para>
/// </remarks>
public sealed class CertificateManager : BackgroundService, IProxyCertificateManager {
    /// <summary>The ceiling on how long the loop sleeps when nothing signals it.</summary>
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromMinutes(5);

    /// <summary>How long after a certificate stops being wanted its files are kept before pruning.</summary>
    private static readonly TimeSpan PruneGrace = TimeSpan.FromDays(30);

    /// <summary>How often the undesired-certificate prune runs. It is housekeeping, not a duty.</summary>
    private static readonly TimeSpan PruneInterval = TimeSpan.FromDays(1);

    /// <summary>The spread applied to a scheduled renewal, so a deployment's certificates do not bunch.</summary>
    private static readonly TimeSpan RenewalJitterWindow = TimeSpan.FromHours(12);

    /// <summary>The spread applied to a backoff, so failures of a common cause do not retry in lockstep.</summary>
    private static readonly TimeSpan BackoffJitterWindow = TimeSpan.FromMinutes(10);

    private readonly CertificateStore _store;
    private readonly CertificateIssuer _issuer;
    private readonly YarpListenerState _listener;
    private readonly RouteStatusUpdater _routeStatus;
    private readonly IAcmeTransportFactory _transport;
    private readonly IOptionsMonitor<WatchtowerOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CertificateManager> _logger;
    private readonly IDisposable? _optionsSubscription;

    /// <summary>Per-host scheduling state. Survives a host leaving and re-entering the desired set.</summary>
    private readonly ConcurrentDictionary<string, HostState> _states = new(StringComparer.Ordinal);

    /// <summary>The attempt currently running for a host, if any — the guard against double orders.</summary>
    private readonly ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);

    /// <summary>Retired sessions, disposed once nothing is using them. See <see cref="GetSession"/>.</summary>
    private readonly ConcurrentQueue<AcmeSession> _retired = new();

    /// <summary>
    /// The certificate thumbprint last written onto each host's route row. Purely a write-suppressor for
    /// <see cref="ProjectHeldCertificatesAsync"/> — see there for why the projection exists at all.
    /// </summary>
    private readonly ConcurrentDictionary<string, string> _projected = new(StringComparer.Ordinal);

    private readonly Lock _sessionGate = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stopping = new();

    private FrozenSet<string> _desired = FrozenSet<string>.Empty;
    private SemaphoreSlim? _orderGate;
    private int _orderGateSize;
    private AcmeSession? _session;
    private string? _sessionKey;
    private DateTimeOffset _lastPruneAt = DateTimeOffset.MinValue;
    private bool _probedWritability;
    private bool _warnedNoHttps;
    private bool _disposed;

    public CertificateManager(
        CertificateStore store,
        CertificateIssuer issuer,
        YarpListenerState listener,
        RouteStatusUpdater routeStatus,
        IAcmeTransportFactory transport,
        IOptionsMonitor<WatchtowerOptions> options,
        TimeProvider time,
        ILoggerFactory loggerFactory) {
        _store = store;
        _issuer = issuer;
        _listener = listener;
        _routeStatus = routeStatus;
        _transport = transport;
        _options = options;
        _time = time;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CertificateManager>();
        // Only to wake the loop. The ACME session itself is rebuilt lazily from a key derived from the
        // settings (see GetSession), so a changed directory URL is picked up whether or not this fires.
        _optionsSubscription = options.OnChange(_ => Nudge());
    }

    private YarpProxyOptions Yarp => _options.CurrentValue.Proxy.Yarp;

    /// <summary>Whether the in-process proxy is the active provider right now.</summary>
    private bool IsActive {
        get {
            var proxy = _options.CurrentValue.Proxy;
            return proxy.Enabled && proxy.ResolveProvider() == ProxyProviderKind.Yarp;
        }
    }

    // ── IProxyCertificateManager ──────────────────────────────────────────────

    /// <inheritdoc />
    public void SetDesiredHosts(IReadOnlyCollection<string> hosts) {
        ArgumentNullException.ThrowIfNull(hosts);
        var next = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in hosts) {
            if (DesiredHosts.TryNormalize(raw, out var host, out var reason)) next.Add(host);
            else
                // Dropped rather than failed: this is called from the route projection, which must not
                // break because one row carries a name no CA would issue for. The route's own status
                // already reports it, and validation refuses such a domain at the point it is typed.
                _logger.LogWarning("Not requesting a certificate for '{Domain}': {Reason}", raw, reason);
        }

        var previous = Interlocked.Exchange(ref _desired, next.ToFrozenSet(StringComparer.Ordinal));
        var diff = DesiredHosts.Diff(previous, next);
        if (diff.IsEmpty) return;

        // A host that is no longer wanted must not keep an order open against the CA's concurrency and
        // rate limits. Its files stay: see the interface contract — this is not a delete.
        foreach (var host in diff.Removed)
            if (_states.TryGetValue(host, out var state)) state.Cancel();

        _logger.LogDebug(
            "Desired certificate hosts changed: +{Added} / -{Removed}.", diff.Added.Count, diff.Removed.Count);
        Nudge();
    }

    /// <inheritdoc />
    public async Task ForgetHostAsync(string host, CancellationToken ct) {
        if (!DesiredHosts.TryNormalize(host, out var name, out var reason))
            throw new ArgumentException(reason, nameof(host));

        if (_states.TryRemove(name, out var state)) state.Cancel();
        // The material is going; a later re-add has to project again rather than assume the row still says so.
        _projected.TryRemove(name, out _);
        // Awaited so the files are not deleted out from under an order that is still writing them.
        if (_inFlight.TryGetValue(name, out var running))
            try {
                await running.WaitAsync(ct);
            } catch (Exception) {
                // The attempt's own failure is not this caller's problem; it has already been recorded.
            }

        // Unlike everything else here, this throws: the caller asked for one specific change and has to
        // be told when it did not happen.
        _store.Forget(name, deleteFiles: true);
        var directory = Path.Combine(_store.RootPath, name);
        if (Directory.Exists(directory))
            throw new IOException($"The certificate directory for {name} could not be removed.");
    }

    // ── Operator-driven and read surfaces ─────────────────────────────────────

    /// <summary>
    /// Issues or renews <paramref name="host"/> right now, bypassing the renewal window and the backoff
    /// — the "Renew now" button.
    /// </summary>
    /// <remarks>
    /// Still goes through the concurrency gate, because the CA's limit on parallel orders is not
    /// something an operator's impatience should be able to exceed. It also ignores the HTTPS-listener
    /// gate that the loop honours: an operator asking explicitly has said they want the attempt made,
    /// and this is the path a Pebble or step-ca run drives.
    /// </remarks>
    public Task<IssueOutcome> RenewNowAsync(string host, CancellationToken ct) {
        if (!DesiredHosts.TryNormalize(host, out var name, out var reason))
            throw new ArgumentException(reason, nameof(host));
        return AttemptAsync(name, ct);
    }

    /// <summary>
    /// Every host the manager has an opinion about: the desired set, plus anything still on disk that
    /// nothing routes to.
    /// </summary>
    public IReadOnlyList<HostCertificateState> Snapshot() {
        var desired = _desired;
        var entries = _store.Entries.ToDictionary(e => e.Host, StringComparer.Ordinal);
        var hosts = new SortedSet<string>(desired, StringComparer.Ordinal);
        foreach (var host in entries.Keys) hosts.Add(host);

        return hosts.Select(host => {
            entries.TryGetValue(host, out var entry);
            _states.TryGetValue(host, out var state);
            return new HostCertificateState(
                Host: host,
                Desired: desired.Contains(host),
                State: Describe(host, entry, state),
                NotBefore: entry?.NotBefore,
                NotAfter: entry?.NotAfter,
                Issuer: entry?.IssuerCommonName,
                LastAttemptAt: state?.LastAttemptAt,
                LastError: state?.LastError,
                NextAttemptAt: state?.NextAttemptAt,
                ConsecutiveFailures: state?.ConsecutiveFailures ?? 0);
        }).ToArray();
    }

    /// <summary>
    /// The five words the UI shows. A host that <em>has</em> a certificate is <c>active</c> even when the
    /// last renewal failed — it is still being served, and the failure is in <c>LastError</c>. Reporting
    /// it as an error would tell an operator their site is down when it is not.
    /// </summary>
    private string Describe(string host, CertificateEntry? entry, HostState? state) {
        if (entry is not null) return "active";
        if (_inFlight.ContainsKey(host)) return "pending";
        if (state?.AwaitingDns == true) return "awaitingDns";
        if (state?.LastError is not null) return "error";
        return state?.LastAttemptAt is null && _desired.Contains(host) ? "pending" : "none";
    }

    // ── The loop ──────────────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _stopping.Token);
        var ct = linked.Token;

        // Off the startup path, and spread: a host restarting after an outage would otherwise open every
        // order it owes in the same second as every other instance that restarted with it.
        var initialDelay = TimeSpan.FromSeconds(5)
                           + TimeSpan.FromSeconds(Random.Shared.Next(0, 16));
        try {
            await Task.Delay(initialDelay, _time, ct);
        } catch (OperationCanceledException) {
            return;
        }

        while (!ct.IsCancellationRequested) {
            try {
                await ReconcileAsync(ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                // The loop is the only thing keeping certificates alive; it does not get to die.
                _logger.LogError(ex, "The certificate reconcile failed; retrying on the next pass.");
            }

            try {
                await _signal.WaitAsync(ReconcileInterval, ct);
            } catch (OperationCanceledException) {
                return;
            }
        }
    }

    /// <summary>
    /// One pass: work out which hosts are due, run those through the concurrency gate, then prune.
    /// </summary>
    /// <remarks>
    /// Public so a test can drive exactly one pass and assert on what the CA was asked for, rather than
    /// starting the service and waiting on a timer. Nothing in production calls it but the loop.
    /// </remarks>
    public async Task ReconcileAsync(CancellationToken ct) {
        DrainRetiredSessions();
        if (!IsActive) return;
        ProbeWritability();

        var desired = _desired;
        if (desired.Count > 0) {
            var now = _time.GetUtcNow();
            // Ahead of the listener gate, and unconditionally: a certificate the store already holds is
            // held whether or not this process is the one that obtained it, and the route row has to say
            // so either way.
            await ProjectHeldCertificatesAsync(desired, now, ct);

            if (!_listener.HttpsBound) {
                if (!_warnedNoHttps) {
                    _warnedNoHttps = true;
                    _logger.LogWarning(
                        "HTTPS listener not bound; certificates would not be served — skipping issuance.");
                }
            } else {
                _warnedNoHttps = false;
                var due = desired.Where(host => IsDue(host, now)).ToArray();
                if (due.Length > 0) await RunAsync(due, ct);
            }
        }

        Prune(desired);
    }

    /// <summary>
    /// Writes "this host has a certificate, valid until" onto the route rows of desired hosts the pass is
    /// about to skip, because the store already holds something usable for them.
    /// </summary>
    /// <remarks>
    /// Without this, the only writer of an <c>Active</c> route row is a successful issuance in this
    /// process — so a certificate an operator hand-placed in the volume, or one issued before the last
    /// restart by a build that never recorded it, is served perfectly while the Routes page insists the
    /// domain is still "Waiting for a certificate" forever. <c>proxy.listCertificates</c> reads the store
    /// directly and reports the same host as <c>active</c>, which is the contradiction operators hit.
    /// <para>
    /// The thumbprint map keeps this to one write per certificate rather than one per five-minute pass:
    /// the row does not change, and rewriting it every tick would churn the database and bury the audit
    /// trail. A renewal changes the thumbprint, so the projection follows it.
    /// </para>
    /// <para>
    /// The manager's own start needs no separate pass: <see cref="CertificateStore"/> loads the volume in
    /// its constructor, and the first reconcile after <see cref="SetDesiredHosts"/> — which the route
    /// projection nudges — sees exactly what that load found.
    /// </para>
    /// </remarks>
    private async Task ProjectHeldCertificatesAsync(
        IReadOnlySet<string> desired, DateTimeOffset now, CancellationToken ct) {
        foreach (var host in desired) {
            var entry = _store.Find(host);
            // Nothing held, or held but on its way out: the issuance path owns those, and claiming
            // "active" for a certificate this pass is about to replace would be a status that lies.
            if (entry is null) continue;
            if (CertificateRenewalPolicy.IsRenewalDue(now, entry.NotBefore, entry.NotAfter)) continue;
            if (_projected.TryGetValue(host, out var written)
                && string.Equals(written, entry.Thumbprint, StringComparison.Ordinal)) continue;

            await _routeStatus.RecordIssuedAsync(host, entry.NotAfter, ct);
            _projected[host] = entry.Thumbprint;
        }
    }

    /// <summary>
    /// Whether a host wants an attempt now: nothing on disk, or a certificate in its renewal window —
    /// and in both cases only once any backoff has elapsed.
    /// </summary>
    private bool IsDue(string host, DateTimeOffset now) {
        if (_inFlight.ContainsKey(host)) return false;
        if (_states.TryGetValue(host, out var state) && state.NextAttemptAt is { } next && now < next) return false;
        var entry = _store.Find(host);
        return entry is null || CertificateRenewalPolicy.IsRenewalDue(now, entry.NotBefore, entry.NotAfter);
    }

    /// <summary>
    /// Runs the due hosts, at most <c>AcmeMaxConcurrentOrders</c> at a time. Each is independent: one
    /// host's failure must not stop the others, which is the whole reason for one order per host.
    /// </summary>
    private Task RunAsync(IReadOnlyList<string> hosts, CancellationToken ct) =>
        Task.WhenAll(hosts.Select(async host => {
            try {
                await AttemptAsync(host, ct);
            } catch (OperationCanceledException) {
                // The host left the desired set, or the process is stopping. Neither is a failure.
            } catch (Exception ex) {
                _logger.LogError(ex, "The certificate attempt for {Host} threw outside the issuer.", host);
            }
        }));

    /// <summary>
    /// One attempt for one host, deduplicated: a second caller for a host already in flight — a nudge
    /// racing the loop, or "Renew now" pressed twice — joins the running attempt instead of opening a
    /// second order for the same name.
    /// </summary>
    private async Task<IssueOutcome> AttemptAsync(string host, CancellationToken ct) {
        var state = _states.GetOrAdd(host, _ => new HostState());
        var mine = new TaskCompletionSource<IssueOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = _inFlight.GetOrAdd(host, mine.Task);
        if (!ReferenceEquals(running, mine.Task)) {
            await running.WaitAsync(ct);
            return ((Task<IssueOutcome>)running).Result;
        }

        try {
            // Linked so SetDesiredHosts can abandon an order for a host nobody wants any more, without
            // touching the caller's own token.
            using var scope = state.Begin(ct, _stopping.Token);
            var gate = OrderGate();
            await gate.WaitAsync(scope.Token);
            IssueOutcome outcome;
            try {
                outcome = await _issuer.IssueAsync(host, GetSession(), scope.Token);
            } finally {
                gate.Release();
            }

            await RecordAsync(host, state, outcome, ct);
            mine.SetResult(outcome);
            return outcome;
        } catch (Exception ex) {
            mine.SetException(ex);
            throw;
        } finally {
            _inFlight.TryRemove(host, out _);
            state.End();
        }
    }

    /// <summary>
    /// Folds one outcome into the host's state, the route row and the schedule. The three have to move
    /// together — a status that says "active" next to a schedule that says "retry in a minute" is how an
    /// operator loses trust in the page.
    /// </summary>
    private async Task RecordAsync(string host, HostState state, IssueOutcome outcome, CancellationToken ct) {
        var now = _time.GetUtcNow();
        state.LastAttemptAt = now;

        switch (outcome) {
            case IssueOutcome.Issued issued:
                state.ConsecutiveFailures = 0;
                state.LastError = null;
                state.AwaitingDns = false;
                state.NextAttemptAt = CertificateRenewalPolicy.ApplyJitter(
                    CertificateRenewalPolicy.RenewalDueAt(issued.NotBefore, issued.NotAfter),
                    RenewalJitterWindow, host);
                await _routeStatus.RecordIssuedAsync(host, issued.NotAfter, ct);
                // The row now matches what was just installed, so the next pass has nothing to project.
                if (_store.Find(host) is { } installed) _projected[host] = installed.Thumbprint;
                break;

            case IssueOutcome.AwaitingDns awaiting:
                // Not a failure on the ladder: the DNS preflight costs nothing and no CA request was
                // made, so checking again on the ordinary cadence is both cheap and what an operator who
                // has just added the record expects.
                state.AwaitingDns = true;
                state.LastError = awaiting.Detail;
                // Reset, not carried over: the preflight made no request, so nothing was spent and the
                // host has not "failed" three times because its DNS record is still propagating. Keeping
                // the count would put the first real attempt — the one made the moment the record
                // appears — straight onto a six-hour rung.
                state.ConsecutiveFailures = 0;
                state.NextAttemptAt = null;
                await _routeStatus.RecordFailedAsync(host, RouteStatus.AwaitingDns, awaiting.Detail, ct);
                break;

            case IssueOutcome.Failed failed:
                state.AwaitingDns = false;
                state.ConsecutiveFailures++;
                state.LastError = failed.Detail;
                state.NextAttemptAt = CertificateRenewalPolicy.ApplyJitter(
                    now + CertificateRenewalPolicy.BackoffFor(state.ConsecutiveFailures, failed.Class, failed.RetryAfter),
                    BackoffJitterWindow, host);
                await _routeStatus.RecordFailedAsync(host, RouteStatus.Error, failed.Detail, ct);
                _logger.LogWarning(
                    "Certificate attempt {Attempt} for {Host} failed ({Class}): {Detail}. Next attempt {Next:u}.",
                    state.ConsecutiveFailures, host, failed.Class, failed.Detail, state.NextAttemptAt);
                break;
        }
    }

    // ── Housekeeping ──────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes certificates nothing routes to any more, once they have been expired long enough to be
    /// useless. Daily, because it is a tidiness concern and reading the whole store more often would be
    /// work for its own sake.
    /// </summary>
    private void Prune(IReadOnlySet<string> desired) {
        var now = _time.GetUtcNow();
        if (now - _lastPruneAt < PruneInterval) return;
        _lastPruneAt = now;
        try {
            var removed = _store.PruneUndesired(desired, PruneGrace);
            if (removed > 0)
                foreach (var host in _states.Keys.Where(h => !desired.Contains(h)).ToArray()) {
                    _states.TryRemove(host, out _);
                    _projected.TryRemove(host, out _);
                }
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not prune undesired certificates.");
        }
    }

    /// <summary>
    /// Proves once that the certificate directory can actually be written to. A read-only volume is a
    /// mundane deployment mistake whose only other symptom is every issuance succeeding at the CA and
    /// then failing to install — which spends rate limit for nothing.
    /// </summary>
    private void ProbeWritability() {
        if (_probedWritability) return;
        _probedWritability = true;
        try {
            Directory.CreateDirectory(_store.RootPath);
            var probe = Path.Combine(_store.RootPath, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        } catch (Exception ex) {
            _logger.LogError(
                ex, "The certificate directory {CertPath} is not writable; no certificate can be installed.",
                _store.RootPath);
        }
    }

    private void Nudge() {
        try {
            _signal.Release();
        } catch (SemaphoreFullException) {
            // A pass is already pending; one is all that is needed.
        } catch (ObjectDisposedException) {
            // Shutting down.
        }
    }

    /// <summary>
    /// The concurrency gate, sized from settings. Rebuilt when the setting changes, which is safe because
    /// it only ever happens between passes — a semaphore in use is never swapped out from under a waiter,
    /// it is simply the previous instance that the running attempts still hold.
    /// </summary>
    private SemaphoreSlim OrderGate() {
        var size = Math.Clamp(Yarp.AcmeMaxConcurrentOrders, 1, 16);
        lock (_sessionGate) {
            if (_orderGate is null || _orderGateSize != size) {
                _orderGate = new SemaphoreSlim(size, size);
                _orderGateSize = size;
            }
            return _orderGate;
        }
    }

    // ── The ACME session ──────────────────────────────────────────────────────

    /// <summary>
    /// The client, account key and CA settings for the directory currently configured, rebuilt whenever
    /// any of them changes.
    /// </summary>
    /// <remarks>
    /// Keyed on the settings rather than driven by an <c>OnChange</c> callback, so there is exactly one
    /// place that decides what "the same CA" means and no window in which a stale client is handed out.
    /// The account directory is derived from the directory URL for the same reason: an ACME account
    /// exists only at the CA that issued it, so pointing Watchtower at a different one has to produce a
    /// fresh key and a fresh registration rather than present one CA's account URL to another.
    /// </remarks>
    private AcmeSession GetSession() {
        var yarp = Yarp;
        var directoryUrl = yarp.AcmeDirectoryUrl?.Trim() ?? "";
        if (!Uri.TryCreate(directoryUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException(
                $"Proxy:Yarp:AcmeDirectoryUrl is not an absolute URL ('{directoryUrl}').");

        var contact = _options.CurrentValue.Proxy.AdminEmail;
        var key = string.Join(
            '\u001f', directoryUrl, yarp.AcmeCaBundlePath ?? "", yarp.AcmeEabKeyId ?? "",
            yarp.AcmeEabHmacKey ?? "", contact ?? "", yarp.AcmeSelfCheckEnabled ? "1" : "0");

        lock (_sessionGate) {
            if (_session is not null && string.Equals(_sessionKey, key, StringComparison.Ordinal))
                return _session;

            var http = _transport.Create(yarp.AcmeCaBundlePath, TimeSpan.FromSeconds(30));
            AcmeAccountKey? account = null;
            try {
                account = AcmeAccountKey.Load(AccountDirectory(directoryUrl), directoryUrl, _logger);
                var client = new AcmeClient(http, account, _time, _loggerFactory.CreateLogger<AcmeClient>());
                var replacement = new AcmeSession(
                    client, account, uri, contact, yarp.AcmeEabKeyId, yarp.AcmeEabHmacKey,
                    yarp.AcmeSelfCheckEnabled);

                // Retired only now that a working replacement exists — and not disposed here either: an
                // order started a moment ago is still holding it, so the next pass disposes it once
                // nothing is in flight. Retiring before the build would leave the manager with no
                // session at all if loading the account key threw.
                if (_session is not null) _retired.Enqueue(_session);
                _session = replacement;
                _sessionKey = key;
                return _session;
            } catch {
                account?.Dispose();
                http.Dispose();
                throw;
            }
        }
    }

    /// <summary>
    /// Where one CA's account material lives: <c>{CertPath}/accounts/{16 hex of SHA-256(directory URL)}</c>.
    /// A hash rather than the URL because the URL is not a path, and a prefix rather than the whole digest
    /// because 64 bits is far more than enough to separate the handful of directories any deployment uses.
    /// </summary>
    private string AccountDirectory(string directoryUrl) {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(directoryUrl));
        return Path.Combine(_store.RootPath, "accounts", Convert.ToHexStringLower(digest.AsSpan(0, 8)));
    }

    private void DrainRetiredSessions() {
        if (!_inFlight.IsEmpty) return;
        while (_retired.TryDequeue(out var session)) session.Dispose();
    }

    // ── Lifetime ──────────────────────────────────────────────────────────────

    public override void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _optionsSubscription?.Dispose();
        _stopping.Cancel();
        base.Dispose();
        lock (_sessionGate) {
            _session?.Dispose();
            _session = null;
        }
        while (_retired.TryDequeue(out var session)) session.Dispose();
        _stopping.Dispose();
        _signal.Dispose();
    }

    /// <summary>
    /// One host's scheduling state and the cancellation of its in-flight attempt. Mutated only from the
    /// attempt path, which is serialised per host by <see cref="_inFlight"/>, and read from the snapshot
    /// — so the fields are plain and the worst a reader sees is a value one attempt out of date.
    /// </summary>
    private sealed class HostState {
        private CancellationTokenSource? _cts;

        public DateTimeOffset? LastAttemptAt { get; set; }
        public string? LastError { get; set; }
        public DateTimeOffset? NextAttemptAt { get; set; }
        public int ConsecutiveFailures { get; set; }

        /// <summary>Whether the last attempt stopped at the DNS preflight — a state, not a failure count.</summary>
        public bool AwaitingDns { get; set; }

        /// <summary>Starts an attempt, whose token this host's <see cref="Cancel"/> can abandon.</summary>
        public CancellationTokenSource Begin(params ReadOnlySpan<CancellationToken> tokens) {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(tokens);
            _cts = cts;
            return cts;
        }

        public void End() => _cts = null;

        /// <summary>Abandons the attempt in flight, if there is one.</summary>
        public void Cancel() {
            try {
                _cts?.Cancel();
            } catch (ObjectDisposedException) {
                // It finished between the read and the cancel.
            }
        }
    }
}
