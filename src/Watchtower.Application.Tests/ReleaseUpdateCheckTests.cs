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
/// The <c>Releases</c>-mode branch of <see cref="StackUpdateService"/> and of
/// <see cref="AutoDeployBackgroundService"/>: release availability instead of registry polling, local
/// drift instead of digest comparison, and the two automation rules a pin and a webhook impose
/// (docs/products/design.md §"Update checks and drift", §"Auto-deploy precedence").
/// </summary>
/// <remarks>
/// <see cref="StackUpdateRevalidationTests"/> owns the <c>Git</c>-mode behaviour and is deliberately
/// untouched by this stage; the assertion that matters most here is the negative one — in release mode
/// no registry is asked at all, which is the per-tenant HEAD storm disappearing.
/// </remarks>
public sealed class ReleaseUpdateCheckTests {
    private const string Project = "shop-prod";
    private const string ApiImage = "ghcr.io/acme/api:latest";
    private const string RunningImageId = "sha256:aaaa";
    private const string RemoteHead = "1111111111111111111111111111111111111111";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── release availability ─────────────────────────────────────────────────

    /// <summary>
    /// The replacement for registry polling: <c>HasUpdates</c> means "a newer release exists", the
    /// version rides along for the badge, and no registry is contacted.
    /// </summary>
    [Fact]
    public async Task Check_ReportsANewerReleaseWithoutAskingAnyRegistry() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var v2 = await host.AddReleaseAsync(productId, "v2");
        var stackId = await host.AddProductStackAsync(Project, productId);
        await host.SetDeployedReleaseAsync(stackId, v1);
        var service = CreateService(host);

        var result = await service.CheckStackAsync(await LoadStackAsync(host, stackId), Ct);

        Assert.True(result.HasUpdates);
        Assert.True(result.HasNewerRelease);
        Assert.Equal(v2, result.AvailableReleaseId);
        Assert.Equal("v2", result.AvailableReleaseVersion);
        Assert.Empty(service.RegistryLookups);
        Assert.Empty(result.OutdatedImages);

        var row = await LoadCheckAsync(host, stackId);
        Assert.Equal(v2, row.AvailableReleaseId);
        Assert.Equal("v2", row.AvailableReleaseVersion);
    }

    /// <summary>A stack already on the newest release has nothing available, and says so.</summary>
    [Fact]
    public async Task Check_ReportsNothingAvailableWhenTheStackIsOnTheNewestRelease() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync(Project, productId);
        await host.SetDeployedReleaseAsync(stackId, v1);

        var result = await CreateService(host).CheckStackAsync(await LoadStackAsync(host, stackId), Ct);

        Assert.False(result.HasUpdates);
        Assert.Null(result.AvailableReleaseId);
    }

    /// <summary>
    /// A new commit is information in this mode, never a trigger: a stack deploys releases, so a commit
    /// no release was built from is not something a redeploy would pick up.
    /// </summary>
    [Fact]
    public async Task Check_RecordsAnUnreleasedCommitWithoutCallingItAnUpdate() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync(Project, productId);
        await host.SetDeployedReleaseAsync(stackId, v1);
        await SetDeployedCommitAsync(host, stackId, ReleaseTestEstate.ReleaseCommit);
        var service = CreateService(host, remoteHead: RemoteHead);

        var result = await service.CheckStackAsync(await LoadStackAsync(host, stackId), Ct);

        Assert.Equal(RemoteHead, result.NewCommitSha);
        // The Git-mode reading of the same row would call this an update. Release mode does not.
        Assert.False(result.HasUpdates);
        Assert.False(result.HasNewerRelease);
        Assert.True(result.HasChanges);
    }

    /// <summary>
    /// A pinned stack is not tracking the branch, so the git head is not asked for — but the newer
    /// release is still computed, because the pin chip shows how far behind it is.
    /// </summary>
    [Fact]
    public async Task Check_SkipsTheGitHeadForAPinnedStackButStillComputesWhatIsAvailable() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var v2 = await host.AddReleaseAsync(productId, "v2");
        var stackId = await host.AddProductStackAsync(Project, productId, pinnedReleaseId: v1);
        await host.SetDeployedReleaseAsync(stackId, v1);
        await SetDeployedCommitAsync(host, stackId, ReleaseTestEstate.ReleaseCommit);
        var service = CreateService(host, remoteHead: RemoteHead);

        var result = await service.CheckStackAsync(await LoadStackAsync(host, stackId), Ct);

        Assert.Empty(service.Git.RemoteHeadLookups);
        Assert.Null(result.NewCommitSha);
        Assert.Equal(v2, result.AvailableReleaseId);
    }

    // ── drift ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drift is local: a container running an image whose digest is not the deployed release's is
    /// named, and nothing is asked of a registry to find that out.
    /// </summary>
    [Fact]
    public async Task Check_NamesAContainerThatIsNotOnTheDeployedReleasesImage() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync(Project, productId);
        await host.SetDeployedReleaseAsync(stackId, v1);
        var service = CreateService(host);
        service.WithRunningContainer("shop-prod-api-1", ApiImage, RunningImageId);
        // Pulled and recreated by hand onto something else entirely.
        service.WithLocalImage(ApiImage, RunningImageId, ReleaseTestEstate.NextApiDigest);

        var result = await service.CheckStackAsync(await LoadStackAsync(host, stackId), Ct);

        Assert.Equal(["shop-prod-api-1"], result.DriftedContainers);
        Assert.Empty(service.RegistryLookups);
        Assert.Equal(["shop-prod-api-1"], (await LoadCheckAsync(host, stackId)).DriftedContainers);
    }

    /// <summary>A container on the release's own digest is not drift, and neither is an unrelated sidecar.</summary>
    [Fact]
    public async Task Check_ReportsNoDriftForTheReleasesOwnImageOrAnUnrelatedSidecar() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync(Project, productId);
        await host.SetDeployedReleaseAsync(stackId, v1);
        var service = CreateService(host);
        service.WithRunningContainer("shop-prod-api-1", ApiImage, RunningImageId);
        service.WithLocalImage(ApiImage, RunningImageId, ReleaseTestEstate.ApiDigest);
        // postgres is not part of the release, so its digest is nobody's business.
        service.WithRunningContainer("shop-prod-db-1", "postgres:16", "sha256:bbbb");
        service.WithLocalImage("postgres:16", "sha256:bbbb", "sha256:cccc");

        var result = await service.CheckStackAsync(await LoadStackAsync(host, stackId), Ct);

        Assert.Empty(result.DriftedContainers);
        Assert.Empty(service.RegistryLookups);
    }

    /// <summary>Nothing deployed yet is nothing to compare against, not drift.</summary>
    [Fact]
    public async Task Check_ReportsNoDriftBeforeAnyReleaseHasBeenDeployed() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync(Project, productId);
        var service = CreateService(host);
        service.WithRunningContainer("shop-prod-api-1", ApiImage, RunningImageId);
        service.WithLocalImage(ApiImage, RunningImageId, ReleaseTestEstate.NextApiDigest);

        var result = await service.CheckStackAsync(await LoadStackAsync(host, stackId), Ct);

        Assert.Empty(result.DriftedContainers);
    }

    // ── Git mode is untouched ────────────────────────────────────────────────

    /// <summary>
    /// The same estate in <c>Git</c> mode still polls the registry and still fills the Git-mode
    /// vocabulary — the release columns are the ones that stay empty. The mutation of every test above.
    /// </summary>
    [Fact]
    public async Task Check_OfAGitModeProduct_StillPollsTheRegistry() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync(Project, productId);
        await host.SetDeployedReleaseAsync(stackId, v1);
        var service = CreateService(host);
        service.WithRunningContainer("shop-prod-api-1", ApiImage, RunningImageId);
        service.WithLocalImage(ApiImage, RunningImageId, ReleaseTestEstate.ApiDigest);
        service.RemoteDigests[ApiImage] = ReleaseTestEstate.NextApiDigest;

        var result = await service.CheckStackAsync(await LoadStackAsync(host, stackId), Ct);

        Assert.Equal([ApiImage], service.RegistryLookups);
        Assert.True(result.HasUpdates);
        Assert.Equal([ApiImage], result.OutdatedImages);
        // A second release exists and the stack is on the first: in Git mode that is not an update.
        Assert.Null(result.AvailableReleaseId);
        Assert.Empty(result.DriftedContainers);
    }

    // ── auto-deploy eligibility ──────────────────────────────────────────────

    /// <summary>
    /// Rule 3: in release mode the webhook is an <c>OnChange</c> stack's trigger, so polling it here
    /// would only race the fan-out to enqueue the identical convergent deploy.
    /// </summary>
    [Fact]
    public void AutoDeploy_SkipsOnChangeStacksInReleaseMode() {
        Assert.False(AutoDeployBackgroundService.IsEligible(
            Stack(ProductReleaseMode.Releases, AutoDeployMode.OnChange)));
        Assert.True(AutoDeployBackgroundService.IsEligible(
            Stack(ProductReleaseMode.Releases, AutoDeployMode.Scheduled)));
    }

    /// <summary>
    /// Rule 2: a pin is an explicit "stay here", so no automatic path may move it — <em>in either
    /// mode</em>. The Git-mode rows are the case that decides the rule: an operator reverting a product
    /// to Git mode while stacks are pinned must not thereby resume branch-head auto-deploys on exactly
    /// the stacks somebody asked to hold still. Clearing the pin is how a stack rejoins automation, and
    /// <c>stacks.setRelease(null)</c> works in Git mode for this reason.
    /// </summary>
    [Theory]
    [InlineData(ProductReleaseMode.Releases, AutoDeployMode.OnChange)]
    [InlineData(ProductReleaseMode.Releases, AutoDeployMode.Scheduled)]
    [InlineData(ProductReleaseMode.Git, AutoDeployMode.OnChange)]
    [InlineData(ProductReleaseMode.Git, AutoDeployMode.Scheduled)]
    public void AutoDeploy_NeverTouchesAPinnedStack(ProductReleaseMode mode, AutoDeployMode autoDeploy) =>
        Assert.False(AutoDeployBackgroundService.IsEligible(Stack(mode, autoDeploy, pinned: true)));

    /// <summary>Rule 4: an unpinned Git-mode stack is exactly what it always was — both modes eligible.</summary>
    [Theory]
    [InlineData(AutoDeployMode.OnChange)]
    [InlineData(AutoDeployMode.Scheduled)]
    public void AutoDeploy_IsUnchangedInGitMode(AutoDeployMode mode) =>
        Assert.True(AutoDeployBackgroundService.IsEligible(Stack(ProductReleaseMode.Git, mode)));

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Stack Stack(ProductReleaseMode mode, AutoDeployMode autoDeploy, bool pinned = false) =>
        new() {
            Name = "s", ComposeProjectName = "s", AutoDeployMode = autoDeploy,
            PinnedReleaseId = pinned ? 1 : null,
            Product = new Product {
                Name = "p", RepositoryUrl = "https://example.invalid/p.git",
                ComposeFilePath = "docker-compose.yml", DefaultBranch = "main", ReleaseMode = mode,
            },
        };

    private static FakeHostUpdateService CreateService(AuthTestHost host, string? remoteHead = null) =>
        new(host.Services.GetRequiredService<DockerEngineClient>(),
            new StubGitCloneService { RemoteHead = remoteHead },
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<StackUpdateService>.Instance);

    private static async Task<Stack> LoadStackAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Stacks.AsNoTracking()
            .Include(s => s.Product)
            .Include(s => s.Template)
            .SingleAsync(s => s.Id == stackId, Ct);
    }

    private static async Task<StackUpdateCheck> LoadCheckAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.StackUpdateChecks.AsNoTracking().SingleAsync(c => c.StackId == stackId, Ct);
    }

    /// <summary>The git-head check needs a baseline to compare against; without one it never runs.</summary>
    private static async Task SetDeployedCommitAsync(AuthTestHost host, int stackId, string commit) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Stacks.Where(s => s.Id == stackId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastDeployedCommit, commit), Ct);
    }

    /// <summary>
    /// A Docker host described without a daemon, and a registry that records every question — the
    /// second is what makes "release mode contacts no registry" an assertion rather than a claim.
    /// </summary>
    private sealed class FakeHostUpdateService(
        DockerEngineClient docker,
        StubGitCloneService git,
        IServiceScopeFactory scopeFactory,
        ILogger<StackUpdateService> logger)
        : StackUpdateService(docker, git, scopeFactory, logger) {
        private readonly Dictionary<string, LocalImageState> _localImages = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<DockerContainerInfo> _containers = [];

        /// <summary>The git double, so a test can see whether the branch head was asked for.</summary>
        public StubGitCloneService Git { get; } = git;

        /// <summary>What the registry would answer, per image reference.</summary>
        public Dictionary<string, string> RemoteDigests { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every image the registry was asked about.</summary>
        public List<string> RegistryLookups { get; } = [];

        public void WithLocalImage(string imageName, string imageId, params string[] repoDigests) =>
            _localImages[imageName] = new LocalImageState(imageId, repoDigests);

        public void WithRunningContainer(string containerName, string imageName, string imageId) =>
            _containers.Add(new DockerContainerInfo {
                Id = $"c{_containers.Count}",
                Names = [$"/{containerName}"],
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

        protected override Task<LocalImageState?> InspectLocalImageAsync(
            string imageName, CancellationToken ct) =>
            Task.FromResult(_localImages.GetValueOrDefault(imageName));

        protected override Task<string?> GetRemoteDigestAsync(
            string imageName, string? username, string? token, CancellationToken ct) {
            RegistryLookups.Add(imageName);
            return Task.FromResult(RemoteDigests.GetValueOrDefault(imageName));
        }
    }
}
