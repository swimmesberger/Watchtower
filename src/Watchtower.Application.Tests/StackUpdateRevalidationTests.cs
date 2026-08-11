using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the local (registry-free) revalidation of cached stack update state: a stack updated on the
/// host with <c>docker compose pull &amp;&amp; docker compose up -d</c> must stop advertising an update
/// without an operator pressing "Check". None of these tests can reach a registry — no Docker daemon is
/// listening in a test, so a revalidation that tried would throw rather than quietly pass.
/// </summary>
public sealed class StackUpdateRevalidationTests {
    private const string OldDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string NewDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";
    private const string OtherNewDigest = "sha256:3333333333333333333333333333333333333333333333333333333333333333";

    private static readonly DateTimeOffset CheckedAt = new(2026, 8, 11, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ImageCarryingTheRecordedDigestLocally_ClearsTheUpdateFlag() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(host, ["app:latest"], new() { ["app:latest"] = NewDigest });
        var service = CreateService(host);
        // The host now has exactly the image the last check said was only in the registry.
        service.LocalDigests["app:latest"] = [NewDigest];

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
    }

    [Fact]
    public async Task ImageStillOnTheOldDigest_KeepsTheUpdateFlag() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(host, ["app:latest"], new() { ["app:latest"] = NewDigest });
        var service = CreateService(host);
        service.LocalDigests["app:latest"] = [OldDigest];

        // Nothing changed, so nothing is written.
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
        service.LocalDigests["app:latest"] = [NewDigest];
        service.LocalDigests["worker:latest"] = [OldDigest];

        var result = await service.RevalidateStackAsync(stackId, Ct);

        Assert.NotNull(result);
        Assert.True(result.HasUpdates);
        var row = await LoadCheckAsync(host, stackId);
        Assert.True(row.HasUpdates);
        Assert.Equal(["worker:latest"], row.OutdatedImages);
        Assert.Equal(OtherNewDigest, Assert.Contains("worker:latest", row.OutdatedImageDigests));
    }

    [Fact]
    public async Task ClearingTheImageUpdate_LeavesAPendingCommitAlone() {
        using var host = AuthTestHost.Start();
        var stackId = await SeedAsync(
            host, ["app:latest"], new() { ["app:latest"] = NewDigest }, newCommitSha: "cafebabe");
        var service = CreateService(host);
        service.LocalDigests["app:latest"] = [NewDigest];

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
        service.LocalDigests["app:latest"] = [NewDigest];

        Assert.Null(await service.RevalidateStackAsync(stackId, Ct));

        // Not even inspected: without a recorded digest there is nothing local to compare against.
        Assert.Empty(service.Inspected);
        var row = await LoadCheckAsync(host, stackId);
        Assert.True(row.HasUpdates);
        Assert.Equal(["app:latest"], row.OutdatedImages);
    }

    [Fact]
    public async Task RepeatedRequests_AreDebouncedPerStack() {
        using var host = AuthTestHost.Start();
        var service = new CountingStackUpdateService(
            host.Services.GetRequiredService<DockerEngineClient>(),
            host.Services.GetRequiredService<GitCloneService>(),
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StackUpdateService>.Instance);
        var revalidator = new StackUpdateRevalidator(
            service, host.Time, NullLogger<StackUpdateRevalidator>.Instance);

        Assert.True(revalidator.Request(1));
        await revalidator.Pending;
        // A dashboard polling every few seconds must not re-inspect the same stack every time.
        Assert.False(revalidator.Request(1));
        Assert.False(revalidator.Request(1));
        // The window is per stack, not global.
        Assert.True(revalidator.Request(2));
        await revalidator.Pending;

        host.Time.Advance(StackUpdateRevalidator.DebounceWindow + TimeSpan.FromSeconds(1));
        Assert.True(revalidator.Request(1));
        await revalidator.Pending;

        Assert.Equal([1, 2, 1], service.Calls);
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static FakeLocalImagesStackUpdateService CreateService(AuthTestHost host) =>
        new(host.Services.GetRequiredService<DockerEngineClient>(),
            host.Services.GetRequiredService<GitCloneService>(),
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StackUpdateService>.Instance);

    /// <summary>Creates a stack whose last check found the given images outdated.</summary>
    private static async Task<int> SeedAsync(
        AuthTestHost host,
        string[] outdatedImages,
        Dictionary<string, string>? digests = null,
        string? newCommitSha = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = "demo",
            RepositoryUrl = "https://example.invalid/demo.git",
            ComposeFilePath = "docker-compose.yml",
            Branch = "main",
            ComposeProjectName = "demo",
            CreatedAt = CheckedAt,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);
        db.StackUpdateChecks.Add(new StackUpdateCheck {
            StackId = stack.Id,
            HasUpdates = true,
            OutdatedImages = outdatedImages,
            OutdatedImageDigests = digests ?? [],
            NewCommitSha = newCommitSha,
            CheckedAt = CheckedAt,
        });
        await db.SaveChangesAsync(Ct);
        return stack.Id;
    }

    private static async Task<StackUpdateCheck> LoadCheckAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.StackUpdateChecks.AsNoTracking().SingleAsync(c => c.StackId == stackId, Ct);
    }

    /// <summary>
    /// Reports what is on the host without a Docker daemon. The inspect call is the only seam
    /// revalidation needs; everything else it does is SQLite, which the test host provides for real.
    /// </summary>
    private sealed class FakeLocalImagesStackUpdateService(
        DockerEngineClient docker,
        GitCloneService git,
        IServiceScopeFactory scopeFactory,
        ILogger<StackUpdateService> logger)
        : StackUpdateService(docker, git, scopeFactory, logger) {
        /// <summary>Repo digests present locally, per image reference.</summary>
        public Dictionary<string, string[]> LocalDigests { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every image this service looked up locally, in order.</summary>
        public List<string> Inspected { get; } = [];

        protected override Task<IReadOnlyList<string>> GetLocalRepoDigestsAsync(
            string imageName, CancellationToken ct) {
            Inspected.Add(imageName);
            return Task.FromResult<IReadOnlyList<string>>(
                LocalDigests.TryGetValue(imageName, out var digests) ? digests : []);
        }
    }

    /// <summary>Records the stacks revalidation was asked for, and does nothing else.</summary>
    private sealed class CountingStackUpdateService(
        DockerEngineClient docker,
        GitCloneService git,
        IServiceScopeFactory scopeFactory,
        ILogger<StackUpdateService> logger)
        : StackUpdateService(docker, git, scopeFactory, logger) {
        private readonly List<int> _calls = [];

        public IReadOnlyList<int> Calls {
            get { lock (_calls) return [.. _calls]; }
        }

        public override Task<StackUpdateResult?> RevalidateStackAsync(int stackId, CancellationToken ct = default) {
            lock (_calls) _calls.Add(stackId);
            return Task.FromResult<StackUpdateResult?>(null);
        }
    }
}
