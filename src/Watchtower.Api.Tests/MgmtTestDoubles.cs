using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Api.Tests;

/// <summary>
/// A deploy queue that accepts work and records it, but never starts a worker.
/// </summary>
/// <remarks>
/// The real worker clones a git repository and shells out to <c>docker compose</c> — neither exists in a
/// test — and it does so on a background thread that would then write to the very database the test is
/// reading, behind its back. What it writes <em>before</em>
/// starting that thread is reproduced faithfully here: the tracking <c>deploy_events</c> row and the
/// stack's queued status, which is exactly what "the deploy was enqueued" means to every caller.
/// </remarks>
public sealed class QueuedOnlyDeployQueueService : DeployQueueService {
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly List<(int StackId, string TriggeredBy)> _calls = [];

    public QueuedOnlyDeployQueueService(
        IServiceScopeFactory scopeFactory,
        GitCloneService git,
        ComposeCliService compose,
        DockerEngineClient docker,
        DeployOutputBroadcaster broadcaster,
        CaddyManager caddy,
        IOptionsMonitor<WatchtowerOptions> options,
        ILogger<DeployQueueService> logger)
        : base(scopeFactory, git, compose, docker, broadcaster, caddy, options, logger) =>
        _scopeFactory = scopeFactory;

    /// <summary>Every enqueue this queue was asked for, in order.</summary>
    public IReadOnlyList<(int StackId, string TriggeredBy)> Calls {
        get { lock (_calls) return [.. _calls]; }
    }

    public override DeployEnqueueResult Enqueue(
        int stackId, string triggeredBy, IReadOnlyList<string>? removeVolumes = null) {
        lock (_calls) _calls.Add((stackId, triggeredBy));

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var deployEvent = new DeployEvent {
            StackId = stackId, TriggeredBy = triggeredBy, Status = "queued", StartedAt = DateTimeOffset.UtcNow,
        };
        db.DeployEvents.Add(deployEvent);
        db.SaveChanges();
        db.Stacks.Where(s => s.Id == stackId)
            .ExecuteUpdate(s => s.SetProperty(x => x.LastDeployStatus, DeployStatus.Queued));
        return new DeployEnqueueResult(deployEvent.Id, "queued");
    }
}

/// <summary>
/// A compose CLI that records <c>down</c> requests and returns a configurable exit code instead of
/// starting a subprocess. Teardown's ordering rules hinge on that exit code, so both outcomes have to be
/// reachable without a Docker daemon.
/// </summary>
/// <remarks>
/// It also samples <see cref="StackProbe"/> at the moment it is called. That is what makes the ordering
/// itself testable rather than merely implied: asserting on the end state cannot tell "containers down,
/// then row deleted" apart from the reverse, and the reverse is the dangerous one — it strands
/// containers that nothing names any more.
/// </remarks>
public sealed class StubComposeCliService()
    : ComposeCliService(Options.Create(new WatchtowerOptions())) {
    private readonly List<(string ProjectName, bool RemoveVolumes)> _downs = [];

    /// <summary>Exit code the next <c>down</c> reports; non-zero makes teardown abort.</summary>
    public int DownExitCode { get; set; }

    /// <summary>Reports whether the tenant's stack row still exists; sampled on each <c>down</c>.</summary>
    public Func<bool>? StackProbe { get; set; }

    /// <summary>What <see cref="StackProbe"/> said at the last <c>down</c>; null when never called.</summary>
    public bool? StackExistedAtDown { get; private set; }

    /// <summary>Every <c>down</c> this service was asked for, in order.</summary>
    public IReadOnlyList<(string ProjectName, bool RemoveVolumes)> Downs {
        get { lock (_downs) return [.. _downs]; }
    }

    public override Task<(int ExitCode, string Output)> DownProjectAsync(
        string projectName, bool removeVolumes, CancellationToken ct) {
        lock (_downs) _downs.Add((projectName, removeVolumes));
        StackExistedAtDown = StackProbe?.Invoke();
        return Task.FromResult((DownExitCode, "stubbed compose down"));
    }

    // Every verb except the stubbed down would reach the process-spawning seams below; a test that
    // grows such a call site must fail loudly instead of shelling out to a real docker CLI.
    protected override Task<(int ExitCode, string Output)> RunAsync(
        string[] args, string? dockerConfigDir, Action<string>? onLine, CancellationToken ct) =>
        throw new InvalidOperationException(
            $"StubComposeCliService only stubs 'down'; unexpected compose invocation: {string.Join(' ', args)}");

    protected override Task<ComposeConfigResult> RunCapturedAsync(string[] args, CancellationToken ct) =>
        throw new InvalidOperationException(
            $"StubComposeCliService only stubs 'down'; unexpected compose invocation: {string.Join(' ', args)}");
}

/// <summary>
/// A proxy provider that records what was asked of it and samples <see cref="StackProbe"/> as it does.
/// Every real provider no-ops while the proxy is disabled — which is how every test host runs it — so
/// counting is the only way to see that a reload was asked for, and the sample is how "reloaded once the
/// routes had actually cascaded away" is distinguished from "reloaded while they were still being served".
/// </summary>
/// <remarks>
/// Deliberately an <see cref="IProxyProvider"/> rather than a subclass of one provider: it stands in for
/// whichever backend <c>Proxy:Provider</c> selects, so these tests assert that the route/tenant plane asks
/// <em>the proxy</em> to reconcile — which is the actual contract — instead of quietly depending on which
/// provider happens to be the default that release.
/// </remarks>
public sealed class RecordingProxyProvider : IProxyProvider {
    private int _applyCount;
    private readonly List<(string Domain, string? Actor)> _forgotten = [];
    private readonly List<int> _connectedStacks = [];

    /// <summary>How many times a proxy reload was requested.</summary>
    public int ApplyCount => Volatile.Read(ref _applyCount);

    /// <summary>The domains a route delete asked the provider to forget, with the recorded actor.</summary>
    public IReadOnlyList<(string Domain, string? Actor)> Forgotten {
        get { lock (_forgotten) return [.. _forgotten]; }
    }

    /// <summary>The stacks whose routed containers were joined to their ingress network.</summary>
    public IReadOnlyList<int> ConnectedStacks {
        get { lock (_connectedStacks) return [.. _connectedStacks]; }
    }

    /// <summary>What <see cref="IsRunningAsync"/> answers. False, like a proxy nothing started.</summary>
    public bool Running { get; set; }

    /// <summary>What <see cref="Enabled"/> answers.</summary>
    public bool Enabled { get; set; }

    /// <summary>Reports whether the tenant's stack row still exists; sampled on each reload.</summary>
    public Func<bool>? StackProbe { get; set; }

    /// <summary>What <see cref="StackProbe"/> said at the last reload; null when never called.</summary>
    public bool? StackExistedAtApply { get; private set; }

    public Task ApplyAsync(CancellationToken ct = default) {
        Interlocked.Increment(ref _applyCount);
        StackExistedAtApply = StackProbe?.Invoke();
        return Task.CompletedTask;
    }

    public Task ForgetDomainAsync(string domain, string? actor, CancellationToken ct = default) {
        lock (_forgotten) _forgotten.Add((domain, actor));
        return Task.CompletedTask;
    }

    public Task ConnectStackAsync(int stackId, CancellationToken ct = default) {
        lock (_connectedStacks) _connectedStacks.Add(stackId);
        return Task.CompletedTask;
    }

    public Task<bool> IsRunningAsync(CancellationToken ct = default) => Task.FromResult(Running);
}
