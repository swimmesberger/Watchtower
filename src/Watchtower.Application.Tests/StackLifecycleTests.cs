using System.Net;
using System.Text;
using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Stacks.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers stack desired state (ADR-0025): <c>stacks.stop</c>/<c>stacks.start</c> through the real
/// generated handler pipelines — including that a failed compose call rolls the state change back —
/// the deploy paths refusing a stopped stack, and the startup reconcile that re-stops containers a
/// Docker restart policy revived.
/// </summary>
public sealed class StackLifecycleTests {
    // ── stacks.stop ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Stop_PersistsTheIntentAndStopsTheComposeProject() {
        var compose = new RecordingProjectComposeCliService();
        using var handler = new RecordingHandler {
            Responder = req => ContainerList(req, ContainerJson("shop", "running")),
        };
        using var host = StartHost(compose, handler, s => s.AddStopStack());
        var stackId = await AddStackAsync(host, "shop", StackDesiredState.Running);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<StopStack.Command, StopStack.Response>(
            scope.ServiceProvider, new StopStack.Command(stackId));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Equal("stopped", result.Value.Stack.DesiredState);
        // The verb works on the project name alone — outside a deploy there is no checkout to
        // point --file at — and `stop` (not `down`) keeps the containers for a fast start.
        Assert.Equal([["compose", "--project-name", "shop", "stop"]], compose.Invocations);
        Assert.Equal(StackDesiredState.Stopped, await DesiredStateAsync(host, stackId));
        await AssertAuditedAsync(host, "stack.stop", "shop");
    }

    [Fact]
    public async Task Stop_SkipsComposeForAProjectWithNoContainers() {
        var compose = new RecordingProjectComposeCliService();
        using var handler = new RecordingHandler(); // /containers/json answers []
        using var host = StartHost(compose, handler, s => s.AddStopStack());
        var stackId = await AddStackAsync(host, "shop", StackDesiredState.Running);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<StopStack.Command, StopStack.Response>(
            scope.ServiceProvider, new StopStack.Command(stackId));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        // "Disabling" a never-deployed stack is a pure database write — nothing to shell out for.
        Assert.Empty(compose.Invocations);
        Assert.Equal(StackDesiredState.Stopped, await DesiredStateAsync(host, stackId));
    }

    [Fact]
    public async Task Stop_LeavesTheIntentUntouchedWhenComposeFails() {
        var compose = new RecordingProjectComposeCliService { ExitCode = 1, Output = "permission denied" };
        using var handler = new RecordingHandler {
            Responder = req => ContainerList(req, ContainerJson("shop", "running")),
        };
        using var host = StartHost(compose, handler, s => s.AddStopStack());
        var stackId = await AddStackAsync(host, "shop", StackDesiredState.Running);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<StopStack.Command, StopStack.Response>(
            scope.ServiceProvider, new StopStack.Command(stackId));

        Assert.False(result.IsSuccess);
        Assert.Contains("permission denied", result.Error.Message);
        // The intent is only written after the stop succeeded: a Stopped row over running
        // containers would make the startup reconcile "finish" a stop that failed.
        Assert.Equal(StackDesiredState.Running, await DesiredStateAsync(host, stackId));
    }

    // ── stacks.start ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_StartsTheExistingContainers() {
        var compose = new RecordingProjectComposeCliService();
        using var handler = new RecordingHandler {
            Responder = req => ContainerList(req, ContainerJson("shop", "exited")),
        };
        using var host = StartHost(compose, handler, s => s.AddStartStack());
        var stackId = await AddStackAsync(host, "shop", StackDesiredState.Stopped);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<StartStack.Command, StartStack.Response>(
            scope.ServiceProvider, new StartStack.Command(stackId));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.True(result.Value.Started);
        Assert.Equal("running", result.Value.Stack.DesiredState);
        Assert.Equal([["compose", "--project-name", "shop", "start"]], compose.Invocations);
        Assert.Equal(StackDesiredState.Running, await DesiredStateAsync(host, stackId));
        await AssertAuditedAsync(host, "stack.start", "shop");
    }

    [Fact]
    public async Task Start_ReenablesWithoutComposeWhenTheProjectHasNoContainers() {
        var compose = new RecordingProjectComposeCliService();
        using var handler = new RecordingHandler(); // /containers/json answers []
        using var host = StartHost(compose, handler, s => s.AddStartStack());
        var stackId = await AddStackAsync(host, "shop", StackDesiredState.Stopped);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<StartStack.Command, StartStack.Response>(
            scope.ServiceProvider, new StartStack.Command(stackId));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        // `compose start` errors on an empty project, so the handler must not call it: the stack
        // is re-enabled and the response tells the operator a deploy is what creates containers.
        Assert.False(result.Value.Started);
        Assert.Empty(compose.Invocations);
        Assert.Equal(StackDesiredState.Running, await DesiredStateAsync(host, stackId));
    }

    // ── deploy paths refuse a stopped stack ──────────────────────────────────

    [Fact]
    public async Task Deploy_IsRejectedWhileTheStackIsStopped() {
        var compose = new RecordingProjectComposeCliService();
        using var handler = new RecordingHandler();
        using var host = StartHost(compose, handler, s => s.AddDeployStack());
        var stackId = await AddStackAsync(host, "shop", StackDesiredState.Stopped);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<DeployStack.Command, DeployStack.Response>(
            scope.ServiceProvider, new DeployStack.Command(stackId));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Contains("stopped", result.Error.Message);
        await using var check = host.Services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Empty(await db.DeployEvents.ToListAsync(Ct)); // nothing was enqueued
    }

    [Fact]
    public async Task TheDeployWorkerFailsARunThatWasQueuedBeforeTheStop() {
        var compose = new RecordingProjectComposeCliService();
        using var handler = new RecordingHandler();
        using var host = StartHost(compose, handler);
        var stackId = await AddStackAsync(host, "shop", StackDesiredState.Stopped);

        // The event models a deploy accepted before (or racing) the stop; the worker must refuse
        // to run it rather than quietly bringing the stack back up.
        int eventId;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var deployEvent = new DeployEvent {
                StackId = stackId, TriggeredBy = "webhook", Status = "queued",
                StartedAt = DateTimeOffset.UtcNow,
            };
            db.DeployEvents.Add(deployEvent);
            await db.SaveChangesAsync(Ct);
            eventId = deployEvent.Id;
        }

        var queue = host.Services.GetRequiredService<DeployQueueService>();
        await queue.ExecuteDeployAsync(stackId, eventId, removeVolumes: null, Ct);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var deployEvent = await db.DeployEvents.SingleAsync(e => e.Id == eventId, Ct);
            Assert.Equal("failed", deployEvent.Status);
            Assert.Contains("stopped", deployEvent.Output);
        }
        Assert.Empty(compose.Invocations); // in particular, no `up`
    }

    // ── the startup reconcile ────────────────────────────────────────────────

    [Fact]
    public async Task TheReconcileRestopsAStoppedStackWhoseContainersCameBack() {
        var compose = new RecordingProjectComposeCliService();
        using var handler = new RecordingHandler {
            Responder = req => ContainerList(req, ContainerJson("shop", "running")),
        };
        using var host = StartHost(compose, handler);
        await AddStackAsync(host, "shop", StackDesiredState.Stopped);

        var restopped = await NewReconciler(host, compose, handler).ReconcileAsync(Ct);

        Assert.Equal(1, restopped);
        Assert.Equal([["compose", "--project-name", "shop", "stop"]], compose.Invocations);
        await AssertAuditedAsync(host, "reconcile.stop", "shop");
    }

    [Fact]
    public async Task TheReconcileLeavesSettledStacksAlone() {
        var compose = new RecordingProjectComposeCliService();
        // The stopped stack's containers are still exited — the common case on every start.
        using var handler = new RecordingHandler {
            Responder = req => ContainerList(req, ContainerJson("shop", "exited")),
        };
        using var host = StartHost(compose, handler);
        await AddStackAsync(host, "shop", StackDesiredState.Stopped);
        await AddStackAsync(host, "blog", StackDesiredState.Running);

        var restopped = await NewReconciler(host, compose, handler).ReconcileAsync(Ct);

        Assert.Equal(0, restopped);
        Assert.Empty(compose.Invocations);
        // Only the stopped stack was even asked about; running stacks are not Watchtower's to touch.
        var containerQueries = handler.Requests.Where(r => r.Contains("/containers/json")).ToList();
        Assert.Single(containerQueries);
        Assert.Contains("shop", Uri.UnescapeDataString(containerQueries[0]));
    }

    [Fact]
    public async Task TheReconcileFinishesThePassAndRethrowsTheFirstFailure() {
        var compose = new RecordingProjectComposeCliService { ExitCode = 1, Output = "daemon still starting" };
        using var handler = new RecordingHandler {
            Responder = req => ContainerList(req, ContainerJson("shop", "restarting")),
        };
        using var host = StartHost(compose, handler);
        await AddStackAsync(host, "shop", StackDesiredState.Stopped);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewReconciler(host, compose, handler).ReconcileAsync(Ct));

        // The rethrow is what makes the caller retry the pass while the daemon comes up.
        Assert.Contains("daemon still starting", failure.Message);
        Assert.Equal([["compose", "--project-name", "shop", "stop"]], compose.Invocations);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthTestHost StartHost(
        RecordingProjectComposeCliService compose, RecordingHandler handler,
        Action<IServiceCollection>? registerHandlers = null) =>
        AuthTestHost.Start(services => {
            services.Replace(ServiceDescriptor.Singleton<ComposeCliService>(compose));
            services.Replace(ServiceDescriptor.Singleton(
                _ => new DockerEngineClient("1.43", handler, TimeSpan.FromMinutes(30))));
            registerHandlers?.Invoke(services);
        });

    private static StackDesiredStateReconciler NewReconciler(
        AuthTestHost host, RecordingProjectComposeCliService compose, RecordingHandler handler) =>
        new(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            host.Services.GetRequiredService<DockerEngineClient>(),
            compose,
            host.Services.GetRequiredService<AuditLog>(),
            NullLogger<StackDesiredStateReconciler>.Instance);

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>().HandleAsync(request, Ct);

    private static async Task<int> AddStackAsync(AuthTestHost host, string name, StackDesiredState desiredState) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = name,
            RepositoryUrl = $"https://github.com/acme/{name}.git",
            ComposeFilePath = "docker-compose.yml",
            Branch = "main",
            ComposeProjectName = name,
            DesiredState = desiredState,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);
        return stack.Id;
    }

    private static async Task<StackDesiredState> DesiredStateAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return (await db.Stacks.AsNoTracking().SingleAsync(s => s.Id == stackId, Ct)).DesiredState;
    }

    private static async Task AssertAuditedAsync(AuthTestHost host, string action, string target) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Category == StackLifecycle.AuditCategory && e.Action == action, Ct);
        Assert.Contains(target, row.Target);
    }

    /// <summary>Answers <c>GET /containers/json</c> with the given container array; everything else falls through.</summary>
    private static HttpResponseMessage? ContainerList(HttpRequestMessage request, string containerJson) =>
        request.RequestUri!.AbsolutePath.EndsWith("/containers/json")
            ? new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent($"[{containerJson}]", Encoding.UTF8, "application/json"),
            }
            : null;

    private static string ContainerJson(string project, string state) => $$$"""
        {"Id":"abc123","Names":["/{{{project}}}-web-1"],"Image":"nginx","State":"{{{state}}}",
         "Status":"{{{state}}}","Labels":{"com.docker.compose.project":"{{{project}}}"}}
        """;
}

/// <summary>
/// A compose CLI that records every assembled argument list at the <c>RunAsync</c> seam and answers
/// with a configurable exit code — so the project-name-only verbs' argument assembly stays under
/// test (see <see cref="ComposeCliService.RunAsync"/> on why the seam sits below the verbs).
/// </summary>
internal sealed class RecordingProjectComposeCliService()
    : ComposeCliService(Options.Create(new WatchtowerOptions())) {
    private readonly List<string[]> _invocations = [];

    /// <summary>Exit code every invocation reports.</summary>
    public int ExitCode { get; set; }

    /// <summary>Captured output every invocation reports.</summary>
    public string Output { get; set; } = "stubbed compose";

    /// <summary>Every argument list that reached the seam, in order.</summary>
    public IReadOnlyList<string[]> Invocations {
        get { lock (_invocations) return [.. _invocations]; }
    }

    protected override Task<(int ExitCode, string Output)> RunAsync(
        string[] args, string? dockerConfigDir, Action<string>? onLine, CancellationToken ct) {
        lock (_invocations) _invocations.Add(args);
        return Task.FromResult((ExitCode, Output));
    }

    protected override Task<ComposeConfigResult> RunCapturedAsync(string[] args, CancellationToken ct) =>
        throw new InvalidOperationException(
            $"Unexpected captured compose invocation: {string.Join(' ', args)}");
}
