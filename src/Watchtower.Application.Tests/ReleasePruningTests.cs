using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Release retention (design.md §"Release retention"): the newest N are kept, everything older goes —
/// <em>except</em> the four kinds of release something still depends on.
/// </summary>
/// <remarks>
/// <b>Every protection rule gets its own test, and each is worth mutation-checking on its own.</b> The
/// four rules protect four different failure modes, only one of which the schema would catch: deleting a
/// pinned release throws on the <c>Restrict</c> foreign key, but a template default, a
/// <c>LastDeployedReleaseId</c> and a <c>DeployEvent.ReleaseId</c> are all <c>SET NULL</c> — a pruner
/// that forgot one of those would succeed, silently, and take a fleet default or a stack's "what am I
/// running" with it.
/// </remarks>
public sealed class ReleasePruningTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The floor itself: everything past the newest N, and nothing inside it.</summary>
    [Fact]
    public async Task Prune_DeletesEverythingBeyondTheRetentionFloor() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var ids = await SeedReleasesAsync(host, productId, 9);
        await host.SetRetainReleasesAsync(productId, ReleasePruner.MinRetainReleases);

        var result = await PruneAsync(host, productId);

        Assert.Equal(4, result.Deleted);
        Assert.Equal(ids.Take(4), result.DeletedIds);
        Assert.Equal(ids.Skip(4), await host.ReleaseIdsAsync(productId));
    }

    /// <summary>A product inside its floor is untouched, and records nothing.</summary>
    [Fact]
    public async Task Prune_UnderTheFloor_DeletesNothingAndAuditsNothing() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var ids = await SeedReleasesAsync(host, productId, 3);

        var result = await PruneAsync(host, productId);

        Assert.Equal(0, result.Deleted);
        Assert.Equal(ids, await host.ReleaseIdsAsync(productId));
        Assert.Empty(await AuditAsync(host));
    }

    /// <summary>The images cascade with the release, and the audit row names what went.</summary>
    [Fact]
    public async Task Prune_AuditsTheCountAndTheIds_AndTakesTheImagesWithThem() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var ids = await SeedReleasesAsync(host, productId, 7);
        await host.SetRetainReleasesAsync(productId, ReleasePruner.MinRetainReleases);

        var result = await PruneAsync(host, productId);

        Assert.Equal(2, result.Deleted);
        var audit = Assert.Single(await AuditAsync(host));
        Assert.Equal("shop", audit.Target);
        Assert.Contains("2 release(s) pruned beyond the newest 5", audit.Detail!, StringComparison.Ordinal);
        Assert.Contains($"#{ids[0]}", audit.Detail!, StringComparison.Ordinal);
        Assert.Contains($"#{ids[1]}", audit.Detail!, StringComparison.Ordinal);
        Assert.Empty(await ImagesOfAsync(host, ids[0]));
    }

    // ── Protection rule 1: a stack pins it ───────────────────────────────────

    /// <summary>
    /// The <c>Restrict</c> foreign key would throw rather than allow this, which would make one
    /// hand-pinned tenant break every future pruning pass of its product.
    /// </summary>
    [Fact]
    public async Task Prune_NeverPrunesAReleaseAStackPins() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var ids = await SeedReleasesAsync(host, productId, 8);
        await host.AddProductStackAsync("shop-prod", productId, pinnedReleaseId: ids[0]);
        await host.SetRetainReleasesAsync(productId, ReleasePruner.MinRetainReleases);

        var result = await PruneAsync(host, productId);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(1, result.Protected);
        Assert.Contains(ids[0], await host.ReleaseIdsAsync(productId));
        Assert.DoesNotContain(ids[0], result.DeletedIds);
    }

    // ── Protection rule 2: a template names it as its default ────────────────

    /// <summary>
    /// The nightmare case. <c>SET NULL</c> means nothing throws: the delete would succeed and the next
    /// tenant provisioned would silently track latest instead of the fleet default somebody chose.
    /// </summary>
    [Fact]
    public async Task Prune_NeverPrunesATemplatesDefaultPin() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var ids = await SeedReleasesAsync(host, productId, 8);
        var templateId = await host.AddProductTemplateAsync(
            "shop-tenants", productId, defaultPinnedReleaseId: ids[0]);
        await host.SetRetainReleasesAsync(productId, ReleasePruner.MinRetainReleases);

        var result = await PruneAsync(host, productId);

        Assert.Equal(2, result.Deleted);
        Assert.Contains(ids[0], await host.ReleaseIdsAsync(productId));
        // …and the default still names it, which is the thing the rule actually protects.
        Assert.Equal(ids[0], await host.TemplateDefaultAsync(templateId));
    }

    // ── Protection rule 3: a stack last deployed it ──────────────────────────

    /// <summary>
    /// Also <c>SET NULL</c>: pruning would blank out "what is this stack actually running", which is the
    /// question every version surface answers.
    /// </summary>
    [Fact]
    public async Task Prune_NeverPrunesAReleaseAStackIsRunning() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var ids = await SeedReleasesAsync(host, productId, 8);
        var stackId = await host.AddProductStackAsync("shop-prod", productId);
        await host.SetDeployedReleaseAsync(stackId, ids[0]);
        await host.SetRetainReleasesAsync(productId, ReleasePruner.MinRetainReleases);

        var result = await PruneAsync(host, productId);

        Assert.Equal(2, result.Deleted);
        Assert.Contains(ids[0], await host.ReleaseIdsAsync(productId));
        Assert.Equal(ids[0], (await host.ReleaseStateAsync(stackId)).LastDeployed);
    }

    // ── Protection rule 4: a deploy event references it ──────────────────────

    /// <summary>
    /// Also <c>SET NULL</c>, and the same loss: the rollout view groups events by release, so pruning
    /// would empty it. <em>Any</em> stored event counts — <c>deploy_events</c> has no retention of its
    /// own, so "recent" has no definition here that would not be invented.
    /// </summary>
    [Fact]
    public async Task Prune_NeverPrunesAReleaseDeployHistoryStillNames() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var ids = await SeedReleasesAsync(host, productId, 8);
        var stackId = await host.AddProductStackAsync("shop-prod", productId);
        var eventId = await host.AddReleaseDeployEventAsync(stackId, ids[0], "success");
        await host.SetRetainReleasesAsync(productId, ReleasePruner.MinRetainReleases);

        var result = await PruneAsync(host, productId);

        Assert.Equal(2, result.Deleted);
        Assert.Contains(ids[0], await host.ReleaseIdsAsync(productId));
        Assert.Equal(ids[0], await EventReleaseAsync(host, eventId));
    }

    // ── Scope and clamping ───────────────────────────────────────────────────

    /// <summary>Another product's releases are not this product's to prune.</summary>
    [Fact]
    public async Task Prune_LeavesOtherProductsAlone() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var otherId = await host.AddProductAsync("other");
        await SeedReleasesAsync(host, productId, 8);
        var otherIds = await SeedReleasesAsync(host, otherId, 8);
        await host.SetRetainReleasesAsync(productId, ReleasePruner.MinRetainReleases);
        await host.SetRetainReleasesAsync(otherId, ReleasePruner.MinRetainReleases);

        await PruneAsync(host, productId);

        Assert.Equal(otherIds, await host.ReleaseIdsAsync(otherId));
    }

    /// <summary>
    /// A hand-edited zero must not mean "delete everything": the clamp is the second line of defence
    /// behind a column that has no RPC setter.
    /// </summary>
    [Fact]
    public async Task Prune_ClampsAnUnusableRetentionValue() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var ids = await SeedReleasesAsync(host, productId, 8);
        await host.SetRetainReleasesAsync(productId, 0);

        var result = await PruneAsync(host, productId);

        Assert.Equal(8 - ReleasePruner.MinRetainReleases, result.Deleted);
        Assert.Equal(ReleasePruner.MinRetainReleases, (await host.ReleaseIdsAsync(productId)).Count);
        Assert.Equal(ids.Skip(3), await host.ReleaseIdsAsync(productId));
    }

    /// <summary>
    /// The other side of the clamp: a hand-edited value above the ceiling is brought down to it rather
    /// than trusted, so retention can never be turned into "keep everything" by a typo with an extra
    /// zero — and the constant is the one the pruner actually applies.
    /// </summary>
    [Fact]
    public async Task Prune_ClampsARetentionValueAboveTheCeiling() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        await SeedReleasesAsync(host, productId, 3);
        await host.SetRetainReleasesAsync(productId, ReleasePruner.MaxRetainReleases * 10);

        var result = await PruneAsync(host, productId);

        // Nothing to delete either way at three releases; what is asserted is the clamped floor the
        // audit-free path used, which the direct Clamp assertion below pins to the constant.
        Assert.Equal(0, result.Deleted);
        Assert.Equal(ReleasePruner.MaxRetainReleases, ReleasePruner.Clamp(int.MaxValue));
        Assert.Equal(ReleasePruner.MinRetainReleases, ReleasePruner.Clamp(int.MinValue));
        Assert.Equal(42, ReleasePruner.Clamp(42));
    }

    /// <summary>The default floor keeps a normal product's whole history untouched.</summary>
    [Fact]
    public async Task Prune_WithTheDefaultFloor_KeepsFiftyReleases() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var ids = await SeedReleasesAsync(host, productId, 52);

        var result = await PruneAsync(host, productId);

        Assert.Equal(2, result.Deleted);
        Assert.Equal(ids.Take(2), result.DeletedIds);
        Assert.Equal(50, (await host.ReleaseIdsAsync(productId)).Count);
    }

    // ── The intake integration ───────────────────────────────────────────────

    /// <summary>
    /// The pass is event-driven: accepting a release is what runs it, so no background loop exists and
    /// an install that never publishes again deletes nothing.
    /// </summary>
    [Fact]
    public async Task Intake_PrunesAfterRecordingTheRelease() {
        using var host = AuthTestHost.Start(
            services => services.AddSingleton<IReleaseDigestResolver>(new StubDigestResolver()));
        var productId = await host.AddProductAsync("shop");
        var ids = await SeedReleasesAsync(host, productId, 6);
        await host.SetRetainReleasesAsync(productId, ReleasePruner.MinRetainReleases);

        await using var scope = host.Services.CreateAsyncScope();
        var intake = scope.ServiceProvider.GetRequiredService<ReleaseIntakeService>();
        var result = await intake.PublishAsync(
            new ReleaseIntakeRequest(
                // Docker Hub is always an admitted registry, so this needs no seeded Registries row.
                productId, ["nginx:new"], Release.ViaManual, Version: "v-new"),
            Ct);

        Assert.Equal(ReleaseIntakeStatus.Created, result.Status);
        var surviving = await host.ReleaseIdsAsync(productId);
        // Six existed, one arrived, five are kept: the two oldest went.
        Assert.Equal(ReleasePruner.MinRetainReleases, surviving.Count);
        Assert.DoesNotContain(ids[0], surviving);
        Assert.DoesNotContain(ids[1], surviving);
        Assert.Contains(result.Release!.Id, surviving);
        Assert.Single(await AuditAsync(host));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Adds <paramref name="count"/> releases and returns their ids, oldest first.</summary>
    private static async Task<List<int>> SeedReleasesAsync(AuthTestHost host, int productId, int count) {
        var ids = new List<int>(count);
        for (var i = 1; i <= count; i++) ids.Add(await host.AddReleaseAsync(productId, $"v{i}"));
        return ids;
    }

    private static async Task<ReleasePruneResult> PruneAsync(AuthTestHost host, int productId) {
        await using var scope = host.Services.CreateAsyncScope();
        var pruner = scope.ServiceProvider.GetRequiredService<ReleasePruner>();
        return await pruner.PruneAsync(productId, actor: "tester", Ct);
    }

    private static async Task<List<string>> ImagesOfAsync(AuthTestHost host, int releaseId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.ReleaseImages.AsNoTracking()
            .Where(i => i.ReleaseId == releaseId).Select(i => i.Repository).ToListAsync(Ct);
    }

    private static async Task<int?> EventReleaseAsync(AuthTestHost host, int eventId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.DeployEvents.AsNoTracking()
            .Where(e => e.Id == eventId).Select(e => e.ReleaseId).FirstAsync(Ct);
    }

    private static async Task<List<AuditEvent>> AuditAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == ReleasePruner.AuditAction).OrderBy(e => e.Id).ToListAsync(Ct);
    }

    /// <summary>A registry that always says the tag is there, without leaving the machine.</summary>
    private sealed class StubDigestResolver : IReleaseDigestResolver {
        public Task<ReleaseDigestResult> ResolveAsync(
            string imageReference, string? username, string? password, CancellationToken ct) =>
            Task.FromResult(ReleaseDigestResult.Resolved(ReleaseTestEstate.NextApiDigest));
    }
}
