using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Stacks.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the local (registry-free) revalidation of cached stack update state: a stack updated on the
/// host with <c>docker compose pull &amp;&amp; docker compose up -d</c> must stop advertising an update
/// without an operator pressing "Check". The fake host below records every registry lookup, so a
/// revalidation that reached for one would be caught rather than quietly pass.
/// </summary>
public sealed class StackUpdateRevalidationTests {
    private const string OldDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string NewDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string OtherNewDigest = "sha256:3333333333333333333333333333333333333333333333333333333333333333";
    private const string OldImageId = "sha256:aaaa";
    private const string NewImageId = "sha256:bbbb";
    private const string Project = "demo";

    private static readonly DateTimeOffset CheckedAt = new(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Revalidation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ImageRunningUnderTheRecordedDigest_ClearsTheUpdateFlag() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(host, ["app:latest"], new() { ["app:latest"] = NewDigest });
        var service = CreateService(host);
        // The stack was pulled and recreated by hand: the image the last check only saw in the
        // registry is on disk, and a container is running it.
        service.WithLocalImage("app:latest", NewImageId, NewDigest);
        service.WithRunningContainer("app:latest", NewImageId);

        var result = await service.RevalidateStackAsync(stackId, Ct);

        Assert.NotNull(result);
        Assert.False(result.HasUpdates);
        Assert.False(result.HasChanges);
        var row = await LoadCheckAsync(host, stackId);
        Assert.False(row.HasUpdates);
        Assert.Empty(row.OutdatedImages);
        Assert.Empty(row.OutdatedImageDigests);
        // A revalidation is not a check: the "last checked" timestamp keeps meaning what it said.
        Assert.Equal(CheckedAt, row.CheckedAt);
        Assert.Empty(service.RegistryLookups);
    }

    [Fact]
    public async Task ImageStillOnTheOldDigest_KeepsTheUpdateFlag() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(host, ["app:latest"], new() { ["app:latest"] = NewDigest });
        var service = CreateService(host);
        service.WithLocalImage("app:latest", OldImageId, OldDigest);
        service.WithRunningContainer("app:latest", OldImageId);

        // Nothing changed, so nothing is written.
        Assert.Null(await service.RevalidateStackAsync(stackId, Ct));

        var row = await LoadCheckAsync(host, stackId);
        Assert.True(row.HasUpdates);
        Assert.Equal(["app:latest"], row.OutdatedImages);
    }

    [Fact]
    public async Task PulledButNotRecreated_KeepsTheUpdateFlag() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(host, ["app:latest"], new() { ["app:latest"] = NewDigest });
        var service = CreateService(host);
        // A bare `docker compose pull`: the new image is on disk…
        service.WithLocalImage("app:latest", NewImageId, NewDigest);
        // …but the container still serving traffic is the old one, which is what the badge reports.
        service.WithRunningContainer("app:latest", OldImageId);

        Assert.Null(await service.RevalidateStackAsync(stackId, Ct));

        var row = await LoadCheckAsync(host, stackId);
        Assert.True(row.HasUpdates);
        Assert.Equal(["app:latest"], row.OutdatedImages);
    }

    [Fact]
    public async Task OnlyTheImageThatWasUpdated_IsCleared() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(
            host,
            ["app:latest", "worker:latest"],
            new() { ["app:latest"] = NewDigest, ["worker:latest"] = OtherNewDigest });
        var service = CreateService(host);
        service.WithLocalImage("app:latest", NewImageId, NewDigest);
        service.WithRunningContainer("app:latest", NewImageId);
        service.WithLocalImage("worker:latest", OldImageId, OldDigest);
        service.WithRunningContainer("worker:latest", OldImageId);

        var result = await service.RevalidateStackAsync(stackId, Ct);

        Assert.NotNull(result);
        Assert.True(result.HasUpdates);
        var row = await LoadCheckAsync(host, stackId);
        Assert.True(row.HasUpdates);
        Assert.Equal(["worker:latest"], row.OutdatedImages);
        Assert.Equal(OtherNewDigest, Assert.Contains("worker:latest", row.OutdatedImageDigests));
    }

    [Fact]
    public async Task ImageWithSeveralRepoDigests_IsClearedByTheMatchingOne() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(host, ["app:latest"], new() { ["app:latest"] = NewDigest });
        var service = CreateService(host);
        // An image pulled under two references carries a repo digest per repository, and the recorded
        // one is not necessarily first — which is why the comparison looks at all of them.
        service.WithLocalImage("app:latest", NewImageId, OtherNewDigest, NewDigest);
        service.WithRunningContainer("app:latest", NewImageId);

        var result = await service.RevalidateStackAsync(stackId, Ct);

        Assert.NotNull(result);
        Assert.False(result.HasUpdates);
        Assert.Empty((await LoadCheckAsync(host, stackId)).OutdatedImages);
    }

    [Fact]
    public async Task ClearingTheImageUpdate_LeavesAPendingCommitAlone() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(
            host, ["app:latest"], new() { ["app:latest"] = NewDigest }, newCommitSha: "cafebabe");
        var service = CreateService(host);
        service.WithLocalImage("app:latest", NewImageId, NewDigest);
        service.WithRunningContainer("app:latest", NewImageId);

        var result = await service.RevalidateStackAsync(stackId, Ct);

        Assert.NotNull(result);
        Assert.False(result.HasUpdates);
        Assert.Equal("cafebabe", result.NewCommitSha);
        // A redeploy still has something to pick up — the commit was never revalidated, only the image.
        Assert.True(result.HasChanges);
        var row = await LoadCheckAsync(host, stackId);
        Assert.False(row.HasUpdates);
        Assert.Equal("cafebabe", row.NewCommitSha);
    }

    [Fact]
    public async Task RowWrittenBeforeDigestsWereRecorded_IsLeftToTheNextFullCheck() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(host, ["app:latest"], digests: []);
        var service = CreateService(host);
        service.WithLocalImage("app:latest", NewImageId, NewDigest);
        service.WithRunningContainer("app:latest", NewImageId);

        Assert.Null(await service.RevalidateStackAsync(stackId, Ct));

        // Not even inspected: without a recorded digest there is nothing local to compare against.
        Assert.Empty(service.Inspected);
        var row = await LoadCheckAsync(host, stackId);
        Assert.True(row.HasUpdates);
        Assert.Equal(["app:latest"], row.OutdatedImages);
    }

    [Fact]
    public async Task ImageFoundByAConcurrentCheck_SurvivesTheWriteBack() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(host, ["app:latest"], new() { ["app:latest"] = NewDigest });
        var service = CreateService(host);
        service.WithLocalImage("app:latest", NewImageId, NewDigest);
        service.WithRunningContainer("app:latest", NewImageId);
        // A full check lands between the snapshot this revalidation started from and its write-back,
        // finding a second outdated image. Writing the snapshot's list back would erase it.
        service.OnInspect = () => RewriteCheckAsync(
            host, stackId,
            ["app:latest", "db:15"],
            new() { ["app:latest"] = NewDigest, ["db:15"] = OtherNewDigest });

        var result = await service.RevalidateStackAsync(stackId, Ct);

        Assert.NotNull(result);
        Assert.True(result.HasUpdates);
        var row = await LoadCheckAsync(host, stackId);
        Assert.Equal(["db:15"], row.OutdatedImages);
        Assert.Equal(OtherNewDigest, Assert.Contains("db:15", row.OutdatedImageDigests));
    }

    [Fact]
    public async Task NewerUpdatePublishedDuringRevalidation_IsNotCleared() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(host, ["app:latest"], new() { ["app:latest"] = NewDigest });
        var service = CreateService(host);
        service.WithLocalImage("app:latest", NewImageId, NewDigest);
        service.WithRunningContainer("app:latest", NewImageId);
        // The operator caught up with one release while a check found the next one. Confirming the
        // first must not clear a flag that now stands for a different, still-pending image.
        service.OnInspect = () => RewriteCheckAsync(
            host, stackId, ["app:latest"], new() { ["app:latest"] = OtherNewDigest });

        Assert.Null(await service.RevalidateStackAsync(stackId, Ct));

        var row = await LoadCheckAsync(host, stackId);
        Assert.True(row.HasUpdates);
        Assert.Equal(["app:latest"], row.OutdatedImages);
        Assert.Equal(OtherNewDigest, Assert.Contains("app:latest", row.OutdatedImageDigests));
    }

    // ── Check path ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckRecordsTheRemoteDigestBehindEachOutdatedImage() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(host, [], digests: [], hasUpdates: false);
        var stack = await LoadStackAsync(host, stackId);
        var service = CreateService(host);
        service.WithRunningContainer("app:latest", OldImageId);
        service.WithLocalImage("app:latest", OldImageId, OldDigest);
        service.RemoteDigests["app:latest"] = NewDigest;

        var result = await service.CheckStackAsync(stack, Ct);

        Assert.True(result.HasUpdates);
        Assert.Equal(["app:latest"], result.OutdatedImages);
        var row = await LoadCheckAsync(host, stackId);
        // Without this the row cannot be revalidated locally later — the whole point of recording it.
        Assert.Equal(NewDigest, Assert.Contains("app:latest", row.OutdatedImageDigests));
    }

    // ── Docker JSON contract ──────────────────────────────────────────────────

    [Fact]
    public void ContainerListJson_BindsTheImageIdTheRunningCheckDependsOn() {
        // Every other test here overrides the container-listing seam, so this is the only place the
        // real wire format is exercised — and this binding fails closed: if "ImageID" stopped mapping
        // onto ImageId (case-insensitive matching switched off, a [JsonPropertyName] added, Docker
        // renaming the field), no container id would ever match a local image, the badge would never
        // clear again, and every other test here would still pass.
        const string json = """
            [{
              "Id": "8dfafdbc3a40",
              "Names": ["/demo-app-1"],
              "Image": "app:latest",
              "ImageID": "sha256:bbbb",
              "State": "running",
              "Status": "Up 2 hours",
              "Labels": { "com.docker.compose.project": "demo" }
            }, {
              "Id": "9cd87474be90",
              "Names": ["/demo-worker-1"],
              "Image": "worker:latest",
              "State": "running",
              "Status": "Up 2 hours",
              "Labels": { "com.docker.compose.project": "demo" }
            }, {
              "Id": "3176a2479c92",
              "Names": ["/demo-cache-1"],
              "Image": "cache:7",
              "ImageID": null,
              "State": "running",
              "Status": "Up 2 hours",
              "Labels": { "com.docker.compose.project": "demo" }
            }]
            """;

        var containers = JsonSerializer.Deserialize(json, DockerJsonContext.Default.ListDockerContainerInfo);

        Assert.NotNull(containers);
        Assert.Equal(3, containers.Count);
        Assert.Equal("sha256:bbbb", containers[0].ImageId);
        Assert.Equal("app:latest", containers[0].Image);
        // Omitted or explicitly null, it must read as "unset" and never as a matchable id — which is
        // why the consumer treats it as possibly-null despite the non-nullable declaration.
        Assert.True(string.IsNullOrEmpty(containers[1].ImageId));
        Assert.True(string.IsNullOrEmpty(containers[2].ImageId));
    }

    // ── Scheduling ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RepeatedRequests_AreDebouncedPerStack() {
        using var host = AuthTestHost.Start();
        var service = CreateScheduledService(host);
        var revalidator = NewRevalidator(host, service);

        Assert.True(revalidator.Request(1));
        await revalidator.Drained;
        // A dashboard polling every few seconds must not re-inspect the same stack every time.
        Assert.False(revalidator.Request(1));
        Assert.False(revalidator.Request(1));
        // The window is per stack, not global.
        Assert.True(revalidator.Request(2));
        await revalidator.Drained;

        host.Time.Advance(StackUpdateRevalidator.DebounceWindow + TimeSpan.FromSeconds(1));
        Assert.True(revalidator.Request(1));
        await revalidator.Drained;

        Assert.Equal([1, 2, 1], service.Calls);
    }

    [Fact]
    public async Task RevalidationSlowerThanTheWindow_IsNotQueuedBehindItself() {
        using var host = AuthTestHost.Start();
        var service = CreateScheduledService(host);
        var revalidator = NewRevalidator(host, service);
        var release = new TaskCompletionSource();
        service.Gate = release.Task;

        Assert.True(revalidator.Request(1));
        await service.Entered;
        host.Time.Advance(StackUpdateRevalidator.DebounceWindow + TimeSpan.FromSeconds(1));
        // The debounce window has passed, but the first pass is still inspecting: a second pass would
        // race it into the same write path.
        Assert.False(revalidator.Request(1));

        service.Gate = null;
        release.SetResult();
        await revalidator.Drained;
        Assert.True(revalidator.Request(1));
        await revalidator.Drained;
        Assert.Equal([1, 1], service.Calls);
    }

    [Fact]
    public async Task QueuedRevalidations_RunOneAtATime() {
        using var host = AuthTestHost.Start();
        var service = CreateScheduledService(host);
        var revalidator = NewRevalidator(host, service);

        // Twenty stacks announced by one list call must not become twenty concurrent Docker inspects.
        for (var stackId = 1; stackId <= 20; stackId++) Assert.True(revalidator.Request(stackId));
        await revalidator.Drained;

        Assert.Equal(20, service.Calls.Count);
        Assert.Equal(1, service.MaxConcurrency);
    }

    // ── Handler wiring ────────────────────────────────────────────────────────

    [Fact]
    public async Task ListStacks_AsksForRevalidationOfStacksWithAPendingImageUpdate() {
        using var host = AuthTestHost.Start();
        var pending = await SeedAsync(host, ["app:latest"], new() { ["app:latest"] = NewDigest });
        var upToDate = await SeedAsync(host, [], digests: [], hasUpdates: false, name: "quiet");
        // Pre-migration row: outdated images but no digest to revalidate against. Announcing it would
        // buy a database read every debounce window and learn nothing.
        var legacy = await SeedAsync(host, ["old:1"], digests: [], name: "legacy");
        var service = CreateScheduledService(host);
        var revalidator = NewRevalidator(host, service);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var result = await new ListStacks(db, revalidator).HandleAsync(new ListStacks.Query(), Ct);
            Assert.True(result.IsSuccess);
            Assert.Equal(3, result.Value.Stacks.Count);
        }
        await revalidator.Drained;

        Assert.Equal([pending], service.Calls);
        Assert.DoesNotContain(upToDate, service.Calls);
        Assert.DoesNotContain(legacy, service.Calls);
    }

    [Fact]
    public async Task GetStack_AsksForRevalidationOfAPendingImageUpdate() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(host, ["app:latest"], new() { ["app:latest"] = NewDigest });
        var service = CreateScheduledService(host);
        var revalidator = NewRevalidator(host, service);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var result = await new GetStack(db, revalidator).HandleAsync(new GetStack.Query(stackId), Ct);
            Assert.True(result.IsSuccess);
            Assert.True(result.Value.Stack.HasUpdates);
        }
        await revalidator.Drained;

        Assert.Equal([stackId], service.Calls);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static FakeHostStackUpdateService CreateService(AuthTestHost host) =>
        new(host.Services.GetRequiredService<DockerEngineClient>(),
            host.Services.GetRequiredService<GitCloneService>(),
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StackUpdateService>.Instance);

    private static RecordingStackUpdateService CreateScheduledService(AuthTestHost host) =>
        new(host.Services.GetRequiredService<DockerEngineClient>(),
            host.Services.GetRequiredService<GitCloneService>(),
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StackUpdateService>.Instance);

    private static StackUpdateRevalidator NewRevalidator(AuthTestHost host, StackUpdateService service) =>
        new(service, host.Time, NullLogger<StackUpdateRevalidator>.Instance);

    /// <summary>Creates a stack whose last check found the given images outdated.</summary>
    private static async Task<int> SeedAsync(
        AuthTestHost host,
        string[] outdatedImages,
        Dictionary<string, string>? digests = null,
        string? newCommitSha = null,
        bool hasUpdates = true,
        string name = "demo") {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = name,
            ComposeProjectName = Project,
            Product = TestProducts.New(name, "https://example.invalid/demo.git"),
            CreatedAt = CheckedAt,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);
        db.StackUpdateChecks.Add(new StackUpdateCheck {
            StackId = stack.Id,
            HasUpdates = hasUpdates,
            OutdatedImages = outdatedImages,
            OutdatedImageDigests = digests ?? [],
            NewCommitSha = newCommitSha,
            CheckedAt = CheckedAt,
        });
        await db.SaveChangesAsync(Ct);
        return stack.Id;
    }

    /// <summary>Stands in for a full check completing while a revalidation is mid-flight.</summary>
    private static async Task RewriteCheckAsync(
        AuthTestHost host, int stackId, string[] outdatedImages, Dictionary<string, string> digests) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.StackUpdateChecks.SingleAsync(c => c.StackId == stackId, Ct);
        row.HasUpdates = true;
        row.OutdatedImages = outdatedImages;
        row.OutdatedImageDigests = digests;
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<StackUpdateCheck> LoadCheckAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.StackUpdateChecks.AsNoTracking().SingleAsync(c => c.StackId == stackId, Ct);
    }

    private static async Task<Stack> LoadStackAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        // With the product, as every production loader does: the source the check resolves lives there.
        return await db.Stacks.AsNoTracking()
            .Include(s => s.Product)
            .Include(s => s.Template)
            .SingleAsync(s => s.Id == stackId, Ct);
    }

    /// <summary>
    /// Describes a Docker host — running containers, local images and, for the check path only, a
    /// registry — without a daemon. Everything else the service does is database work, which the test host
    /// provides for real.
    /// </summary>
    private sealed class FakeHostStackUpdateService(
        DockerEngineClient docker,
        GitCloneService git,
        IServiceScopeFactory scopeFactory,
        ILogger<StackUpdateService> logger)
        : StackUpdateService(docker, git, scopeFactory, logger) {
        private readonly Dictionary<string, LocalImageState> _localImages = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<DockerContainerInfo> _containers = [];

        /// <summary>What the registry would answer, per image reference.</summary>
        public Dictionary<string, string> RemoteDigests { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every image looked up locally, in order.</summary>
        public List<string> Inspected { get; } = [];

        /// <summary>Every image the registry was asked about — empty is the point on the local path.</summary>
        public List<string> RegistryLookups { get; } = [];

        /// <summary>Runs on the first local inspect; stands in for something else writing the row.</summary>
        public Func<Task>? OnInspect { get; set; }

        public void WithLocalImage(string imageName, string imageId, params string[] repoDigests) =>
            _localImages[imageName] = new LocalImageState(imageId, repoDigests);

        public void WithRunningContainer(string imageName, string imageId) =>
            _containers.Add(new DockerContainerInfo {
                Id = $"c{_containers.Count}",
                Names = [$"/{Project}-{_containers.Count}"],
                Image = imageName,
                ImageId = imageId,
                State = "running",
                Status = "Up 2 hours",
                Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = Project },
            });

        protected override Task<IReadOnlyList<DockerContainerInfo>> GetProjectContainersAsync(
            string composeProjectName, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DockerContainerInfo>>([
                .. _containers.Where(c => c.Labels["com.docker.compose.project"] == composeProjectName)]);

        protected override async Task<LocalImageState?> InspectLocalImageAsync(
            string imageName, CancellationToken ct) {
            if (Inspected.Count == 0 && OnInspect is { } hook) await hook();
            Inspected.Add(imageName);
            return _localImages.GetValueOrDefault(imageName);
        }

        protected override Task<string?> GetRemoteDigestAsync(
            string imageName, string? username, string? token, CancellationToken ct) {
            RegistryLookups.Add(imageName);
            return Task.FromResult(RemoteDigests.GetValueOrDefault(imageName));
        }
    }

    /// <summary>
    /// Records the stacks revalidation was asked for and how many ran at once, without touching the
    /// database — the scheduling tests are about the queue, not about what it queues.
    /// </summary>
    private sealed class RecordingStackUpdateService(
        DockerEngineClient docker,
        GitCloneService git,
        IServiceScopeFactory scopeFactory,
        ILogger<StackUpdateService> logger)
        : StackUpdateService(docker, git, scopeFactory, logger) {
        private readonly List<int> _calls = [];
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _running;
        private int _maxConcurrency;

        /// <summary>Held open to keep a revalidation "in flight" for as long as a test needs.</summary>
        public Task? Gate { get; set; }

        /// <summary>Completes when the first revalidation has started.</summary>
        public Task Entered => _entered.Task;

        public IReadOnlyList<int> Calls {
            get { lock (_calls) return [.. _calls]; }
        }

        /// <summary>The most revalidations ever running at the same moment.</summary>
        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);

        public override async Task<StackUpdateResult?> RevalidateStackAsync(
            int stackId, CancellationToken ct = default) {
            lock (_calls) _calls.Add(stackId);
            var running = Interlocked.Increment(ref _running);
            InterlockedMax(ref _maxConcurrency, running);
            _entered.TrySetResult();
            try {
                if (Gate is { } gate) await gate;
                else await Task.Yield();
                return null;
            } finally {
                Interlocked.Decrement(ref _running);
            }
        }

        private static void InterlockedMax(ref int target, int value) {
            var seen = Volatile.Read(ref target);
            while (seen < value) {
                var previous = Interlocked.CompareExchange(ref target, value, seen);
                if (previous == seen) return;
                seen = previous;
            }
        }
    }
}
