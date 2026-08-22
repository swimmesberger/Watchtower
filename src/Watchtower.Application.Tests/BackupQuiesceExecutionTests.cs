using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The quiesce step of a run against a recorded daemon (ADR-0019): each dependency level goes down at
/// once, stops carry the short grace, pauses are written to the safety-net table before they happen and
/// cleared once thawed, a failure part-way resumes what is already down, and a process that died inside
/// the window thaws its containers on the next start. Everything the daemon sees is asserted by path,
/// because the ordering of those paths <em>is</em> the downtime contract.
/// </summary>
public sealed class BackupQuiesceExecutionTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Api = "/v1.43/containers";

    // ── Builders ─────────────────────────────────────────────────────────────

    private static BackupContainer C(
        string name, string[]? volumes = null, string[]? dependsOn = null, string? stop = null) =>
        new($"{name}-id", name, name, 1, true, volumes ?? [], dependsOn ?? [], null, stop);

    private static BackupPlan Plan(
        IReadOnlyList<BackupContainer> containers, string[] volumes,
        BackupQuiesceMode mode = BackupQuiesceMode.Stop) =>
        BackupPlan.Create(new BackupPlanRequest(containers, volumes, StopContainers: true, QuiesceMode: mode));

    private static Stack TheStack() => new() {
        Name = "web-app",
        RepositoryUrl = "https://example.com/web-app.git",
        ComposeFilePath = "docker-compose.yml",
        Branch = "main",
        ComposeProjectName = "web-app",
    };

    /// <summary>A host whose Docker client talks to <paramref name="handler"/> instead of the socket.</summary>
    private static AuthTestHost Host(HttpMessageHandler handler) =>
        AuthTestHost.Start(services => services.Replace(ServiceDescriptor.Singleton(
            _ => new DockerEngineClient("1.43", handler, TimeSpan.FromMinutes(30)))));

    private static async Task<List<string>> PausedRowsAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.BackupPausedContainers.OrderBy(p => p.Id).Select(p => p.ContainerId).ToListAsync(Ct);
    }

    private static async Task InsertPausedRowsAsync(AuthTestHost host, params string[] containerIds) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.BackupPausedContainers.AddRange(containerIds.Select(id => new BackupPausedContainer {
            ContainerId = id, ContainerName = id, StackName = "web-app", PausedAt = DateTimeOffset.UtcNow,
        }));
        await db.SaveChangesAsync(Ct);
    }

    private static HttpResponseMessage Inspect(string id, string status) => new(HttpStatusCode.OK) {
        Content = new StringContent(
            $$$"""{"Id":"{{{id}}}","Image":"sha256:x","Config":{"Image":"img"},"State":{"Status":"{{{status}}}","ExitCode":0}}""",
            Encoding.UTF8, "application/json"),
    };

    // ── Quiesce + resume ─────────────────────────────────────────────────────

    [Fact]
    public async Task LevelsGoDownInOrder_StopsCarryTheGrace_AndResumeRunsTheLevelsBackwards() {
        using var handler = new RecordingHandler();
        using var host = Host(handler);
        var service = host.Services.GetRequiredService<BackupService>();
        // web (pause) → api (explicit stop) → db (pause by stack default): three levels, mixed modes.
        var plan = Plan(
            [C("web", ["static"], dependsOn: ["api"]),
             C("api", ["uploads"], dependsOn: ["db"], stop: "true"),
             C("db", ["pgdata"])],
            ["pgdata", "static", "uploads"],
            BackupQuiesceMode.Pause);
        var log = new List<string>();

        var quiesced = await service.QuiescePlannedContainersAsync(
            plan, TheStack(), new BackupOptions { StopTimeoutSeconds = 3 }, log.Add, Ct);

        Assert.Equal(
            [$"{Api}/web-id/pause", $"{Api}/api-id/stop?t=3", $"{Api}/db-id/pause"],
            handler.Requests);
        Assert.Equal(2, quiesced.PausedCount);
        Assert.Equal(1, quiesced.StoppedCount);
        // The safety net holds both pauses while the window is open.
        Assert.Equal(["web-id", "db-id"], await PausedRowsAsync(host));

        handler.Requests.Clear();
        await service.ResumeContainersAsync(quiesced, log.Add);

        Assert.Equal(
            [$"{Api}/db-id/unpause", $"{Api}/api-id/start", $"{Api}/web-id/unpause"],
            handler.Requests);
        Assert.Empty(await PausedRowsAsync(host));
        Assert.Contains(log, l => l.StartsWith("Quiescing 3 of 3 running container(s): stopping api; pausing web, db", StringComparison.Ordinal));
        Assert.Contains("Unpaused db", log);
        Assert.Contains("Restarted api", log);
    }

    [Fact]
    public async Task TheStopGraceIsClampedAndDefaultsToFive() {
        using var handler = new RecordingHandler();
        using var host = Host(handler);
        var service = host.Services.GetRequiredService<BackupService>();
        var plan = Plan([C("db", ["pgdata"])], ["pgdata"]);

        var quiesced = await service.QuiescePlannedContainersAsync(plan, TheStack(), new BackupOptions(), _ => { }, Ct);
        await service.ResumeContainersAsync(quiesced, _ => { });
        handler.Requests.Clear();
        quiesced = await service.QuiescePlannedContainersAsync(
            plan, TheStack(), new BackupOptions { StopTimeoutSeconds = 0 }, _ => { }, Ct);
        await service.ResumeContainersAsync(quiesced, _ => { });

        Assert.Equal([$"{Api}/db-id/stop?t=1", $"{Api}/db-id/start"], handler.Requests);
    }

    [Fact]
    public async Task ContainersOfOneLevelAreQuiescedConcurrently() {
        // The gate releases a stop only once the level's other stop has arrived too: a sequential
        // executor would wait on the first forever, so this fails fast rather than by timing.
        using var handler = new ConcurrencyGateHandler(expectedConcurrent: 2, matching: "/stop");
        using var host = Host(handler);
        var service = host.Services.GetRequiredService<BackupService>();
        var plan = Plan(
            [C("api", ["uploads"], dependsOn: ["db"]), C("worker", ["spool"], dependsOn: ["db"]), C("db", ["pgdata"])],
            ["pgdata", "spool", "uploads"]);

        var quiesced = await service.QuiescePlannedContainersAsync(
            plan, TheStack(), new BackupOptions(), _ => { }, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(3, quiesced.StoppedCount);
        // The level boundary still holds: db's stop was only issued after both dependents were down.
        Assert.Equal($"{Api}/db-id/stop?t=5", handler.Requests[^1]);
        Assert.Equal(
            new HashSet<string> { $"{Api}/api-id/stop?t=5", $"{Api}/worker-id/stop?t=5" },
            handler.Requests.Take(2).ToHashSet());
    }

    [Fact]
    public async Task AFailureInALaterLevelResumesWhatIsAlreadyDown_AndRethrows() {
        using var handler = new RecordingHandler {
            Responder = r => r.RequestUri!.AbsolutePath.EndsWith("/db-id/stop")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null,
        };
        using var host = Host(handler);
        var service = host.Services.GetRequiredService<BackupService>();
        var plan = Plan(
            [C("web", ["static"], dependsOn: ["db"], stop: "pause"), C("db", ["pgdata"])],
            ["pgdata", "static"]);
        var log = new List<string>();

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.QuiescePlannedContainersAsync(plan, TheStack(), new BackupOptions(), log.Add, Ct));

        Assert.Equal([$"{Api}/web-id/pause", $"{Api}/db-id/stop?t=5", $"{Api}/web-id/unpause"], handler.Requests);
        Assert.Contains("Unpaused web", log);
        // The unpaused container's row is gone; nothing is left for the reconcile to find.
        Assert.Empty(await PausedRowsAsync(host));
    }

    [Fact]
    public async Task AFailureInsideALevelStillResumesThatLevelsSiblings() {
        using var handler = new RecordingHandler {
            Responder = r => r.RequestUri!.AbsolutePath.EndsWith("/b-id/stop")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null,
        };
        using var host = Host(handler);
        var service = host.Services.GetRequiredService<BackupService>();
        var plan = Plan([C("a", ["va"]), C("b", ["vb"]), C("c", ["vc"])], ["va", "vb", "vc"]);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.QuiescePlannedContainersAsync(plan, TheStack(), new BackupOptions(), _ => { }, Ct));

        var started = handler.Requests.Where(r => r.EndsWith("/start")).ToHashSet();
        Assert.Equal([$"{Api}/a-id/start", $"{Api}/c-id/start"], started.Order());
        Assert.DoesNotContain($"{Api}/b-id/start", handler.Requests);
    }

    [Fact]
    public async Task AContainerWhoseUnpauseFailsKeepsItsSafetyNetRow() {
        using var handler = new RecordingHandler {
            Responder = r => r.RequestUri!.AbsolutePath.EndsWith("/web-id/unpause")
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : null,
        };
        using var host = Host(handler);
        var service = host.Services.GetRequiredService<BackupService>();
        var plan = Plan([C("web", ["static"]), C("api", ["uploads"])], ["static", "uploads"], BackupQuiesceMode.Pause);
        var log = new List<string>();

        var quiesced = await service.QuiescePlannedContainersAsync(plan, TheStack(), new BackupOptions(), log.Add, Ct);
        await service.ResumeContainersAsync(quiesced, log.Add);

        Assert.Contains(log, l => l.StartsWith("WARNING: failed to unpause web", StringComparison.Ordinal));
        // api thawed and is forgotten; web is still frozen and stays on record for the next start.
        Assert.Equal(["web-id"], await PausedRowsAsync(host));
    }

    // ── Startup reconcile ────────────────────────────────────────────────────

    [Fact]
    public async Task TheReconcileUnpausesOnlyWhatIsStillPaused_AndClearsTheTable() {
        using var handler = new RecordingHandler {
            Responder = r => r.RequestUri!.AbsolutePath switch {
                var p when p.EndsWith("/frozen-id/json") => Inspect("frozen-id", "paused"),
                var p when p.EndsWith("/thawed-id/json") => Inspect("thawed-id", "running"),
                var p when p.EndsWith("/gone-id/json") => new HttpResponseMessage(HttpStatusCode.NotFound),
                _ => null,
            },
        };
        using var host = Host(handler);
        await InsertPausedRowsAsync(host, "frozen-id", "thawed-id", "gone-id");
        var service = host.Services.GetRequiredService<BackupService>();

        var unpaused = await service.UnpauseLeftoversAsync(Ct);

        Assert.Equal(1, unpaused);
        Assert.Contains($"{Api}/frozen-id/unpause", handler.Requests);
        Assert.DoesNotContain($"{Api}/thawed-id/unpause", handler.Requests);
        Assert.DoesNotContain($"{Api}/gone-id/unpause", handler.Requests);
        Assert.Empty(await PausedRowsAsync(host));

        // Audited, so the trail shows the stack was frozen across a restart.
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var audit = await db.AuditEvents.SingleAsync(e => e.Action == "reconcile.unpause", Ct);
        Assert.Equal("web-app", audit.Target);
        Assert.Contains("frozen-id", audit.Detail);
    }

    [Fact]
    public async Task TheReconcileIsANoOpWithAnEmptyTable() {
        using var handler = new RecordingHandler();
        using var host = Host(handler);

        Assert.Equal(0, await host.Services.GetRequiredService<BackupService>().UnpauseLeftoversAsync(Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AReconcileThatCannotReachOneContainerKeepsItsRowAndThrows() {
        using var handler = new RecordingHandler {
            Responder = r => r.RequestUri!.AbsolutePath switch {
                var p when p.EndsWith("/frozen-id/json") => Inspect("frozen-id", "paused"),
                var p when p.EndsWith("/broken-id/json") => new HttpResponseMessage(HttpStatusCode.InternalServerError),
                _ => null,
            },
        };
        using var host = Host(handler);
        await InsertPausedRowsAsync(host, "frozen-id", "broken-id");
        var service = host.Services.GetRequiredService<BackupService>();

        await Assert.ThrowsAsync<HttpRequestException>(() => service.UnpauseLeftoversAsync(Ct));

        // The reachable one was still thawed and forgotten; the other waits for the retry.
        Assert.Contains($"{Api}/frozen-id/unpause", handler.Requests);
        Assert.Equal(["broken-id"], await PausedRowsAsync(host));
    }

    /// <summary>
    /// Answers every request OK, but holds each request matching <paramref name="matching"/> until
    /// <paramref name="expectedConcurrent"/> of them are in flight at once — a deterministic proof of
    /// concurrency: a sequential caller never gets its first one back.
    /// </summary>
    private sealed class ConcurrencyGateHandler(int expectedConcurrent, string matching) : HttpMessageHandler {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            lock (Requests) Requests.Add(request.RequestUri!.PathAndQuery);
            if (request.RequestUri!.AbsolutePath.EndsWith(matching, StringComparison.Ordinal)) {
                if (Interlocked.Increment(ref _arrived) == expectedConcurrent) _gate.TrySetResult();
                // Only the first level is gated; later stops (db) pass once the gate has opened.
                await _gate.Task.WaitAsync(cancellationToken);
            }
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }
}
