using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Products.Handlers;
using Watchtower.Application.Modules.Stacks;
using Watchtower.Application.Modules.Stacks.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The operator-facing half of stage 4: <c>stacks.setRelease</c>, <c>products.deployRelease</c>, the
/// pinned-release delete guard, and the manual <c>releaseMode</c> override on <c>products.update</c>.
/// </summary>
/// <remarks>
/// Handlers are invoked directly, like the rest of the module suites. The deploy queue is a recording
/// double throughout: these tests are about what is written and what is refused, not about what a
/// deploy then does.
/// </remarks>
public sealed class ReleasePinRpcTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── stacks.setRelease ────────────────────────────────────────────────────

    /// <summary>Pinning writes the pin, audits before → after, and enqueues an operator-triggered deploy.</summary>
    [Fact]
    public async Task SetRelease_PinsAuditsAndDeploys() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var v2 = await host.AddReleaseAsync(productId, "v2");
        var stackId = await host.AddProductStackAsync("shop-prod", productId);

        var (result, queue) = await SetReleaseAsync(host, stackId, v1);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(v1, (await host.ReleaseStateAsync(stackId)).Pinned);
        Assert.Equal(StackMapping.TrackingPinned, result.Value.Stack.TrackingMode);
        Assert.Equal(new StackReleaseRefDto(v1, "v1"), result.Value.Stack.PinnedRelease);
        Assert.True(result.Value.Deployed);
        Assert.Equal([(stackId, DeployTriggers.ReleaseManual)], queue.Enqueued);

        var audit = Assert.Single(await AuditAsync(host, SetStackRelease.PinAction));
        Assert.Equal("shop-prod", audit.Target);
        Assert.Contains("latest → v1", audit.Detail!, StringComparison.Ordinal);
        // v2 exists and is newer; the pin is what it says it is.
        Assert.NotEqual(v2, (await host.ReleaseStateAsync(stackId)).Pinned);
    }

    /// <summary>Clearing the pin puts the stack back on latest and deploys it — "catch up" is one action.</summary>
    [Fact]
    public async Task SetRelease_WithNull_ClearsThePinAndDeploysLatest() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync("shop-prod", productId, pinnedReleaseId: v1);

        var (result, queue) = await SetReleaseAsync(host, stackId, releaseId: null);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Null((await host.ReleaseStateAsync(stackId)).Pinned);
        Assert.Equal(StackMapping.TrackingLatest, result.Value.Stack.TrackingMode);
        Assert.Null(result.Value.Stack.PinnedRelease);
        Assert.Equal([(stackId, DeployTriggers.ReleaseManual)], queue.Enqueued);

        var audit = Assert.Single(await AuditAsync(host, SetStackRelease.UnpinAction));
        Assert.Contains("v1 (#" + v1 + ") → latest", audit.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pre-flight: a digest the registry no longer has is a <c>409</c> naming it, and nothing is
    /// written. A rollback that discovered this at <c>compose pull</c> would already be halfway done.
    /// </summary>
    [Fact]
    public async Task SetRelease_RefusesWithAConflictWhenAnImageIsGone() {
        using var host = StartHost(new StubDigestResolver { Answer = ReleaseDigestResult.NotFound });
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync("shop-prod", productId);

        var (result, queue) = await SetReleaseAsync(host, stackId, v1);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Contains(ReleaseTestEstate.ApiDigest, result.Error.Message, StringComparison.Ordinal);
        Assert.Null((await host.ReleaseStateAsync(stackId)).Pinned);
        Assert.Empty(queue.Enqueued);
        Assert.Empty(await AuditAsync(host, SetStackRelease.PinAction));
    }

    /// <summary>
    /// A registry that did not answer is a different refusal: nothing was concluded, so the operator is
    /// told to retry rather than told the release is broken.
    /// </summary>
    [Fact]
    public async Task SetRelease_AsksForARetryWhenTheRegistryDidNotAnswer() {
        using var host = StartHost(new StubDigestResolver { Answer = ReleaseDigestResult.Unavailable });
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync("shop-prod", productId);

        var (result, _) = await SetReleaseAsync(host, stackId, v1);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.BusinessRule, result.Error.Kind);
        Assert.Null((await host.ReleaseStateAsync(stackId)).Pinned);
    }

    /// <summary>
    /// A release of another product pins digests this stack's compose file can never match, so it would
    /// deploy unpinned while looking pinned.
    /// </summary>
    [Fact]
    public async Task SetRelease_RefusesAReleaseOfAnotherProduct() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop");
        var otherId = await host.AddProductAsync("other");
        var foreign = await host.AddReleaseAsync(otherId, "v1");
        var stackId = await host.AddProductStackAsync("shop-prod", productId);

        var (result, _) = await SetReleaseAsync(host, stackId, foreign);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Null((await host.ReleaseStateAsync(stackId)).Pinned);
    }

    /// <summary>
    /// Pinning a Git-mode product would write a value nothing reads — the resolver never consults the
    /// pin in that mode — so the stack would keep deploying branch heads while claiming to be pinned.
    /// </summary>
    [Fact]
    public async Task SetRelease_RefusesToPinAGitModeProduct() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync("shop-prod", productId);

        var (result, _) = await SetReleaseAsync(host, stackId, v1);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
    }

    /// <summary>…but unpinning always works, so a mode revert never strands a pin nobody can clear.</summary>
    [Fact]
    public async Task SetRelease_AllowsUnpinningAGitModeProduct() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync("shop-prod", productId, pinnedReleaseId: v1);
        await host.SetReleaseModeAsync(productId, ProductReleaseMode.Git);

        var (result, _) = await SetReleaseAsync(host, stackId, releaseId: null);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Null((await host.ReleaseStateAsync(stackId)).Pinned);
    }

    /// <summary>Save without deploy: the pin is recorded and nothing is enqueued.</summary>
    [Fact]
    public async Task SetRelease_WritesThePinWithoutDeployingWhenAsked() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync("shop-prod", productId);

        var (result, queue) = await SetReleaseAsync(host, stackId, v1, deploy: false);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(v1, (await host.ReleaseStateAsync(stackId)).Pinned);
        Assert.False(result.Value.Deployed);
        Assert.Empty(queue.Enqueued);
    }

    /// <summary>
    /// A stopped stack is disabled, not misconfigured: the pin is written and the deploy is skipped, so
    /// "pin it, then start it" works instead of being refused.
    /// </summary>
    [Fact]
    public async Task SetRelease_PinsAStoppedStackWithoutDeployingIt() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync(
            "shop-prod", productId, desiredState: StackDesiredState.Stopped);

        var (result, queue) = await SetReleaseAsync(host, stackId, v1);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(v1, (await host.ReleaseStateAsync(stackId)).Pinned);
        Assert.False(result.Value.Deployed);
        Assert.Empty(queue.Enqueued);
    }

    // ── products.deployRelease ───────────────────────────────────────────────

    [Fact]
    public async Task DeployRelease_RollsTheLatestOutAndAuditsTheCount() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        await host.AddReleaseAsync(productId, "v1");
        var v2 = await host.AddReleaseAsync(productId, "v2");
        var a = await host.AddProductStackAsync("a", productId, AutoDeployMode.Off);
        var b = await host.AddProductStackAsync("b", productId, AutoDeployMode.OnChange);

        var (result, queue) = await DeployReleaseAsync(host, productId);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(v2, result.Value.ReleaseId);
        Assert.Equal("v2", result.Value.Version);
        Assert.Equal(2, result.Value.StacksEnqueued);
        Assert.Equal(
            [(a, DeployTriggers.ReleaseManual), (b, DeployTriggers.ReleaseManual)], queue.Enqueued);

        var audit = Assert.Single(await AuditAsync(host, DeployRelease.AuditAction));
        Assert.Equal("shop/v2", audit.Target);
        Assert.Contains("2 stack(s) enqueued", audit.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The optional guard: a caller that names the release it believed was newest is refused rather
    /// than quietly rolling out a different one.
    /// </summary>
    [Fact]
    public async Task DeployRelease_RefusesAReleaseThatIsNoLongerTheNewest() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        await host.AddReleaseAsync(productId, "v2");
        await host.AddProductStackAsync("a", productId, AutoDeployMode.OnChange);

        var (result, queue) = await DeployReleaseAsync(host, productId, releaseId: v1);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Contains("v2", result.Error.Message, StringComparison.Ordinal);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task DeployRelease_RefusesAGitModeProductAndAProductWithNoReleases() {
        using var host = AuthTestHost.Start();
        var gitId = await host.AddProductAsync("git-mode", ProductReleaseMode.Git);
        var emptyId = await host.AddProductAsync("no-releases");

        var (git, _) = await DeployReleaseAsync(host, gitId);
        var (empty, _) = await DeployReleaseAsync(host, emptyId);

        Assert.Equal(ErrorKind.Conflict, git.Error.Kind);
        Assert.Equal(ErrorKind.BusinessRule, empty.Error.Kind);
    }

    // ── products.deleteRelease ───────────────────────────────────────────────

    /// <summary>
    /// The guard stage 3 owed: deleting a pinned release would silently flip its stacks back to
    /// latest-tracking, so it is refused with the stacks named.
    /// </summary>
    [Fact]
    public async Task DeleteRelease_RefusesWhileAStackPinsIt() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        await host.AddProductStackAsync("shop-prod", productId, pinnedReleaseId: v1);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await ActivatorUtilities.CreateInstance<DeleteRelease>(scope.ServiceProvider)
            .HandleAsync(new DeleteRelease.Command(v1), Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Contains("'shop-prod'", result.Error.Message, StringComparison.Ordinal);
        Assert.True(await ReleaseExistsAsync(host, v1));
    }

    /// <summary>
    /// A release something merely <em>deployed</em> is still deletable: those references are records of
    /// the past and are <c>SET NULL</c>, or retention could never prune anything.
    /// </summary>
    [Fact]
    public async Task DeleteRelease_AllowsDeletingAReleaseAStackOnlyDeployed() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var stackId = await host.AddProductStackAsync("shop-prod", productId);
        await host.SetDeployedReleaseAsync(stackId, v1);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await ActivatorUtilities.CreateInstance<DeleteRelease>(scope.ServiceProvider)
            .HandleAsync(new DeleteRelease.Command(v1), Ct);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.False(await ReleaseExistsAsync(host, v1));
        Assert.Null((await host.ReleaseStateAsync(stackId)).LastDeployed);
    }

    // ── products.update: the manual mode override ────────────────────────────

    /// <summary>An operator can revert a product to Git mode, and the change is attributed.</summary>
    [Fact]
    public async Task UpdateProduct_RevertsToGitModeAndAuditsIt() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        await host.AddReleaseAsync(productId, "v1");

        var result = await UpdateModeAsync(host, productId, "git");

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("git", result.Value.Product.ReleaseMode);
        var audit = Assert.Single(await AuditAsync(host, ReleaseIntakeService.ModeChangeAction));
        Assert.Contains("Releases → Git", audit.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// Releases mode with no release would render a Version panel with nothing in it and leave every
    /// stack resolving null — refused rather than allowed to look broken.
    /// </summary>
    [Fact]
    public async Task UpdateProduct_RefusesReleaseModeForAProductWithNoReleases() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);

        var result = await UpdateModeAsync(host, productId, "releases");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Empty(await AuditAsync(host, ReleaseIntakeService.ModeChangeAction));
    }

    [Fact]
    public async Task UpdateProduct_RefusesAModeItCannotRead() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");

        var result = await UpdateModeAsync(host, productId, "sometimes");

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    /// <summary>Omitting the field leaves the mode alone and records no mode change.</summary>
    [Fact]
    public async Task UpdateProduct_LeavesTheModeAloneWhenTheFieldIsAbsent() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");

        var result = await UpdateModeAsync(host, productId, mode: null);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("releases", result.Value.Product.ReleaseMode);
        Assert.Empty(await AuditAsync(host, ReleaseIntakeService.ModeChangeAction));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static AuthTestHost StartHost(IReleaseDigestResolver resolver) =>
        AuthTestHost.Start(services => services.AddSingleton(resolver));

    private static async Task<(Result<SetStackRelease.Response> Result, RecordingDeployQueue Queue)>
        SetReleaseAsync(AuthTestHost host, int stackId, int? releaseId, bool deploy = true) {
        await using var scope = host.Services.CreateAsyncScope();
        var queue = RecordingDeployQueue.Create(host);
        var handler = ActivatorUtilities.CreateInstance<SetStackRelease>(scope.ServiceProvider, queue);
        var result = await handler.HandleAsync(new SetStackRelease.Command(stackId, releaseId, deploy), Ct);
        return (result, queue);
    }

    private static async Task<(Result<DeployRelease.Response> Result, RecordingDeployQueue Queue)>
        DeployReleaseAsync(AuthTestHost host, int productId, int? releaseId = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var queue = RecordingDeployQueue.Create(host);
        var handler = ActivatorUtilities.CreateInstance<DeployRelease>(
            scope.ServiceProvider, new ReleaseRolloutService(db, queue));
        var result = await handler.HandleAsync(new DeployRelease.Command(productId, releaseId), Ct);
        return (result, queue);
    }

    private static async Task<Result<UpdateProduct.Response>> UpdateModeAsync(
        AuthTestHost host, int productId, string? mode) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var product = await db.Products.AsNoTracking().FirstAsync(p => p.Id == productId, Ct);
        return await ActivatorUtilities.CreateInstance<UpdateProduct>(scope.ServiceProvider)
            .HandleAsync(
                new UpdateProduct.Command(
                    productId, product.Name, product.RepositoryUrl, product.ComposeFilePath,
                    product.DefaultBranch, ReleaseMode: mode),
                Ct);
    }

    private static async Task<bool> ReleaseExistsAsync(AuthTestHost host, int releaseId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Releases.AsNoTracking().AnyAsync(r => r.Id == releaseId, Ct);
    }

    private static async Task<List<AuditEvent>> AuditAsync(AuthTestHost host, string action) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == action).OrderBy(e => e.Id).ToListAsync(Ct);
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";

    /// <summary>A registry that answers whatever the test says, without leaving the machine.</summary>
    private sealed class StubDigestResolver : IReleaseDigestResolver {
        /// <summary>A fixed outcome for every lookup; the default is "still there".</summary>
        public ReleaseDigestResult? Answer { get; init; }

        public Task<ReleaseDigestResult> ResolveAsync(
            string imageReference, string? username, string? password, CancellationToken ct) =>
            Task.FromResult(Answer ?? ReleaseDigestResult.Resolved(ReleaseTestEstate.ApiDigest));
    }
}
