using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Tenancy;
using Watchtower.Application.Modules.Tenancy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Stage 6's tenant half: <c>templates.setTenantsRelease</c> and the one field ADR-0026 copies at
/// provisioning (<see cref="StackTemplate.DefaultPinnedReleaseId"/>).
/// </summary>
/// <remarks>
/// Handlers are invoked directly and the deploy queue is a recording double, like the other release
/// suites: what is asserted is what was written and what was refused, never what a deploy then does.
/// </remarks>
public sealed class ReleaseTenantPolicyTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── templates.setTenantsRelease ──────────────────────────────────────────

    /// <summary>
    /// The whole contract in one test: every current tenant is pinned, the template default is written
    /// for the tenants that do not exist yet, and the audit row names both halves.
    /// </summary>
    [Fact]
    public async Task SetTenantsRelease_PinsEveryTenantAndStoresTheTemplateDefault() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var a = await host.AddProductStackAsync("shop-a", productId, templateId: templateId);
        var b = await host.AddProductStackAsync("shop-b", productId, templateId: templateId);
        // A stack of the same product that is *not* a tenant of the template must be left alone.
        var standalone = await host.AddProductStackAsync("shop-prod", productId);

        var (result, queue) = await SetTenantsReleaseAsync(host, templateId, v1);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(2, result.Value.Result.TenantCount);
        Assert.Equal(v1, await host.TemplateDefaultAsync(templateId));
        Assert.Equal(v1, (await host.ReleaseStateAsync(a)).Pinned);
        Assert.Equal(v1, (await host.ReleaseStateAsync(b)).Pinned);
        Assert.Null((await host.ReleaseStateAsync(standalone)).Pinned);
        // deploy defaults to false: a fleet redeploying is an event to opt into.
        Assert.Empty(queue.Enqueued);
        Assert.Equal(0, result.Value.Result.Deployed);

        var audit = Assert.Single(await AuditAsync(host, SetTenantsRelease.AuditAction));
        Assert.Equal("shop-tenants", audit.Target);
        Assert.Contains("latest → v1", audit.Detail!, StringComparison.Ordinal);
        Assert.Contains("2 tenant(s) and the template default", audit.Detail!, StringComparison.Ordinal);
    }

    /// <summary>The deploy flag enqueues one operator-triggered deploy per tenant.</summary>
    [Fact]
    public async Task SetTenantsRelease_WithDeploy_EnqueuesEveryRunningTenant() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var a = await host.AddProductStackAsync("shop-a", productId, templateId: templateId);
        var stopped = await host.AddProductStackAsync(
            "shop-b", productId, desiredState: StackDesiredState.Stopped, templateId: templateId);

        var (result, queue) = await SetTenantsReleaseAsync(host, templateId, v1, deploy: true);

        Assert.True(result.IsSuccess, Describe(result));
        // Both were pinned; only the running one was deployed. A stopped tenant is disabled, not
        // misconfigured — refusing the call over it would make "pin it, then start it" impossible.
        Assert.Equal(2, result.Value.Result.TenantCount);
        Assert.Equal(1, result.Value.Result.Deployed);
        Assert.Equal(v1, (await host.ReleaseStateAsync(stopped)).Pinned);
        Assert.Equal([(a, DeployTriggers.ReleaseManual)], queue.Enqueued);
        Assert.Equal(queue.EventIds, result.Value.Result.DeployEventIds);
    }

    /// <summary>Null clears every pin and the default — the fleet goes back to tracking latest.</summary>
    [Fact]
    public async Task SetTenantsRelease_WithNull_ClearsEveryPinAndTheDefault() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId, defaultPinnedReleaseId: v1);
        var a = await host.AddProductStackAsync(
            "shop-a", productId, pinnedReleaseId: v1, templateId: templateId);

        var (result, _) = await SetTenantsReleaseAsync(host, templateId, releaseId: null);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Null(await host.TemplateDefaultAsync(templateId));
        Assert.Null((await host.ReleaseStateAsync(a)).Pinned);
        Assert.Null(result.Value.Result.Release);

        var audit = Assert.Single(await AuditAsync(host, SetTenantsRelease.AuditAction));
        Assert.Contains($"v1 (#{v1}) → latest", audit.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A release of another product pins digests these tenants' compose file can never match, so every
    /// one of them would deploy unpinned while the roster called them pinned.
    /// </summary>
    [Fact]
    public async Task SetTenantsRelease_RefusesAReleaseOfAnotherProduct() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop");
        var otherId = await host.AddProductAsync("other");
        var foreign = await host.AddReleaseAsync(otherId, "v1");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var a = await host.AddProductStackAsync("shop-a", productId, templateId: templateId);

        var (result, queue) = await SetTenantsReleaseAsync(host, templateId, foreign);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Null(await host.TemplateDefaultAsync(templateId));
        Assert.Null((await host.ReleaseStateAsync(a)).Pinned);
        Assert.Empty(queue.Enqueued);
    }

    /// <summary>
    /// Pinning a Git-mode product's fleet would write a value nothing reads: the resolver answers null
    /// before it ever looks at a pin (the <c>stacks.setRelease</c> precedent).
    /// </summary>
    [Fact]
    public async Task SetTenantsRelease_RefusesToPinAGitModeProduct() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop", ProductReleaseMode.Git);
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);

        var (result, _) = await SetTenantsReleaseAsync(host, templateId, v1);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Null(await host.TemplateDefaultAsync(templateId));
    }

    /// <summary>…but clearing always works, so a mode revert never strands a fleet nobody can free.</summary>
    [Fact]
    public async Task SetTenantsRelease_AllowsClearingAGitModeProduct() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId, defaultPinnedReleaseId: v1);
        var a = await host.AddProductStackAsync(
            "shop-a", productId, pinnedReleaseId: v1, templateId: templateId);
        await host.SetReleaseModeAsync(productId, ProductReleaseMode.Git);

        var (result, _) = await SetTenantsReleaseAsync(host, templateId, releaseId: null);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Null(await host.TemplateDefaultAsync(templateId));
        Assert.Null((await host.ReleaseStateAsync(a)).Pinned);
    }

    /// <summary>
    /// The pre-flight, which matters more for a fleet than for one stack: a garbage-collected digest
    /// would otherwise fail at <c>compose pull</c> on every tenant, one after another.
    /// </summary>
    [Fact]
    public async Task SetTenantsRelease_RefusesWithAConflictWhenAnImageIsGone() {
        using var host = StartHost(new StubDigestResolver { Answer = ReleaseDigestResult.NotFound });
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var a = await host.AddProductStackAsync("shop-a", productId, templateId: templateId);

        var (result, queue) = await SetTenantsReleaseAsync(host, templateId, v1);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Contains(ReleaseTestEstate.ApiDigest, result.Error.Message, StringComparison.Ordinal);
        Assert.Null(await host.TemplateDefaultAsync(templateId));
        Assert.Null((await host.ReleaseStateAsync(a)).Pinned);
        Assert.Empty(queue.Enqueued);
        Assert.Empty(await AuditAsync(host, SetTenantsRelease.AuditAction));
    }

    /// <summary>A registry that did not answer concludes nothing, so the operator is told to retry.</summary>
    [Fact]
    public async Task SetTenantsRelease_AsksForARetryWhenTheRegistryDidNotAnswer() {
        using var host = StartHost(new StubDigestResolver { Answer = ReleaseDigestResult.Unavailable });
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);

        var (result, _) = await SetTenantsReleaseAsync(host, templateId, v1);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.BusinessRule, result.Error.Kind);
        Assert.Null(await host.TemplateDefaultAsync(templateId));
    }

    /// <summary>A template with no tenants still records the default, for the tenants that come later.</summary>
    [Fact]
    public async Task SetTenantsRelease_WithNoTenants_StillWritesTheDefault() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);

        var (result, _) = await SetTenantsReleaseAsync(host, templateId, v1, deploy: true);

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(0, result.Value.Result.TenantCount);
        Assert.Equal(v1, await host.TemplateDefaultAsync(templateId));
    }

    /// <summary>
    /// The two writes are one transaction: a failure on the second must not leave the tenants pinned
    /// while the template default still names the old release — the state that would make the next
    /// tenant join a fleet it disagrees with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure is forced without a production seam, by making the handler's own
    /// <c>SaveChangesAsync</c> throw: a rename staged on the <em>same scoped change tracker</em> collides
    /// with the unique index on <c>stack_templates.name</c>, and the handler's save is what flushes it.
    /// By then the tenants' <c>ExecuteUpdate</c> has already run — so if it were its own implicit
    /// transaction it would already be committed, and this test's first assertion would fail.
    /// </para>
    /// <para>
    /// Mutation-checked by removing the <c>BeginTransactionAsync</c> wrapper: the tenant then stays
    /// pinned to a release the template default never got, which is the half-applied fleet the wrapper
    /// exists to prevent.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SetTenantsRelease_RollsTheTenantWriteBackWhenTheDefaultWriteFails() {
        using var host = StartHost(new StubDigestResolver());
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await host.AddProductTemplateAsync("taken-name", productId);
        var a = await host.AddProductStackAsync("shop-a", productId, templateId: templateId);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var queue = RecordingDeployQueue.Create(host);
        var handler = ActivatorUtilities.CreateInstance<SetTenantsRelease>(scope.ServiceProvider, queue);

        // Staged, not saved: the handler resolves the same scoped context, so its SaveChanges flushes
        // this rename alongside the template default — straight into the unique index.
        var tracked = await db.StackTemplates.FirstAsync(t => t.Id == templateId, Ct);
        tracked.Name = "taken-name";

        await Assert.ThrowsAnyAsync<DbUpdateException>(
            () => handler.HandleAsync(new SetTenantsRelease.Command(templateId, v1, false), Ct).AsTask());

        // Neither half landed, and nothing was enqueued over a write that did not happen.
        Assert.Null((await host.ReleaseStateAsync(a)).Pinned);
        Assert.Null(await host.TemplateDefaultAsync(templateId));
        Assert.Empty(queue.Enqueued);
    }

    // ── provisioning copies the default ──────────────────────────────────────

    /// <summary>
    /// The one field family ADR-0026 copies: a tenant provisioned under a fleet default starts on it
    /// rather than on latest, which is what makes "the fleet is on 1.4.0" survive the next tenant.
    /// </summary>
    [Fact]
    public async Task Provision_CopiesTheTemplatesDefaultPinOntoTheNewTenant() {
        using var host = AuthTestHost.Start(WithQueuedOnlyDeploys);
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        await host.AddReleaseAsync(productId, "v2");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId, defaultPinnedReleaseId: v1);

        var result = await ProvisionAsync(host, templateId, "acme");

        Assert.Equal(TenantProvisionStatus.Created, result.Status);
        // v2 exists and is newer; the tenant is on the fleet default, not on latest.
        Assert.Equal(v1, (await host.ReleaseStateAsync(result.Tenant!.StackId)).Pinned);
        Assert.Equal(TenancyMapping.TrackingPinned, result.Tenant.TrackingMode);
        Assert.Equal(new ReleaseRefDto(v1, "v1"), result.Tenant.PinnedRelease);
    }

    /// <summary>No default means what it always meant: the tenant tracks latest.</summary>
    [Fact]
    public async Task Provision_WithNoDefault_LeavesTheTenantTrackingLatest() {
        using var host = AuthTestHost.Start(WithQueuedOnlyDeploys);
        var productId = await host.AddProductAsync("shop");
        await host.AddReleaseAsync(productId, "v1");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);

        var result = await ProvisionAsync(host, templateId, "acme");

        Assert.Equal(TenantProvisionStatus.Created, result.Status);
        Assert.Null((await host.ReleaseStateAsync(result.Tenant!.StackId)).Pinned);
        Assert.Equal(TenancyMapping.TrackingLatest, result.Tenant.TrackingMode);
        Assert.Null(result.Tenant.PinnedRelease);
    }

    /// <summary>
    /// Copied, not referenced: moving the template's default afterwards leaves the tenant that inherited
    /// the old one exactly where it was. That asymmetry is why the field is copied at all.
    /// </summary>
    [Fact]
    public async Task Provision_TakesTheDefaultOnce_SoALaterFleetMoveDoesNotFollowIt() {
        using var host = AuthTestHost.Start(WithQueuedOnlyDeploys);
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId, defaultPinnedReleaseId: v1);
        var tenant = await ProvisionAsync(host, templateId, "acme");
        var v2 = await host.AddReleaseAsync(productId, "v2");

        // The default moves through a direct write, so this asserts the *copy*, not setTenantsRelease's
        // own fleet write (which deliberately repins the tenants that already exist).
        await SetTemplateDefaultAsync(host, templateId, v2);

        Assert.Equal(v1, (await host.ReleaseStateAsync(tenant.Tenant!.StackId)).Pinned);
    }

    // ── templates.listTenants: the roster's version column ───────────────────

    [Fact]
    public async Task ListTenants_CarriesEachTenantsVersionState() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var v2 = await host.AddReleaseAsync(productId, "v2");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var pinned = await host.AddProductStackAsync(
            "shop-a", productId, pinnedReleaseId: v1, templateId: templateId, tenantSlug: "a");
        var tracking = await host.AddProductStackAsync(
            "shop-b", productId, templateId: templateId, tenantSlug: "b");
        await host.SetDeployedReleaseAsync(tracking, v2);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await ActivatorUtilities.CreateInstance<ListTenants>(scope.ServiceProvider)
            .HandleAsync(new ListTenants.Query(templateId), Ct);

        Assert.True(result.IsSuccess, Describe(result));
        var rows = result.Value.Tenants.ToDictionary(t => t.StackId);
        Assert.Equal(TenancyMapping.TrackingPinned, rows[pinned].TrackingMode);
        Assert.Equal(new ReleaseRefDto(v1, "v1"), rows[pinned].PinnedRelease);
        Assert.Null(rows[pinned].LastDeployedRelease);
        Assert.Equal(TenancyMapping.TrackingLatest, rows[tracking].TrackingMode);
        Assert.Null(rows[tracking].PinnedRelease);
        Assert.Equal(new ReleaseRefDto(v2, "v2"), rows[tracking].LastDeployedRelease);
    }

    /// <summary>
    /// Every template read projects the fleet default. It is a navigation, so a handler that forgets
    /// the <c>Include</c> answers "no default" over a template that has one — silently, and the caller
    /// caches it.
    /// </summary>
    [Fact]
    public async Task TemplateReads_AllProjectTheFleetDefault() {
        using var host = AuthTestHost.Start();
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var templateId = await host.AddProductTemplateAsync(
            "shop-tenants", productId, defaultPinnedReleaseId: v1);
        var expected = new ReleaseRefDto(v1, "v1");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        var get = await ActivatorUtilities.CreateInstance<GetTemplate>(scope.ServiceProvider)
            .HandleAsync(new GetTemplate.Query(templateId), Ct);
        var list = await ActivatorUtilities.CreateInstance<ListTemplates>(scope.ServiceProvider)
            .HandleAsync(new ListTemplates.Query(), Ct);
        var template = await db.StackTemplates.AsNoTracking().FirstAsync(t => t.Id == templateId, Ct);
        var update = await ActivatorUtilities.CreateInstance<UpdateTemplate>(scope.ServiceProvider)
            .HandleAsync(
                new UpdateTemplate.Command(
                    templateId, template.Name,
                    // The source fields are read-only projections since ADR-0026; posting the effective
                    // values back is what the frontend does and what update compares against.
                    RepositoryUrl: $"https://example.invalid/shop.git",
                    ComposeFilePath: TestProducts.ComposeFilePath,
                    Branch: TestProducts.DefaultBranch,
                    CredentialId: null,
                    template.DomainPattern, template.TargetServiceName, template.TargetPort,
                    BaseEnvVars: null),
                Ct);

        Assert.Equal(expected, get.Value.Template.DefaultPinnedRelease);
        Assert.Equal(expected, Assert.Single(list.Value.Templates).DefaultPinnedRelease);
        Assert.Equal(expected, update.Value.Template.DefaultPinnedRelease);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Accept deploys without running them; the provisioning suite's double.</summary>
    private static readonly Action<IServiceCollection> WithQueuedOnlyDeploys = services => {
        services.RemoveAll<DeployQueueService>();
        services.AddSingleton<DeployQueueService>(
            sp => ActivatorUtilities.CreateInstance<QueuedOnlyDeployQueueService>(sp));
    };

    private static AuthTestHost StartHost(IReleaseDigestResolver resolver) =>
        AuthTestHost.Start(services => services.AddSingleton(resolver));

    private static async Task<(Result<SetTenantsRelease.Response> Result, RecordingDeployQueue Queue)>
        SetTenantsReleaseAsync(AuthTestHost host, int templateId, int? releaseId, bool deploy = false) {
        await using var scope = host.Services.CreateAsyncScope();
        var queue = RecordingDeployQueue.Create(host);
        var handler = ActivatorUtilities.CreateInstance<SetTenantsRelease>(scope.ServiceProvider, queue);
        var result = await handler.HandleAsync(
            new SetTenantsRelease.Command(templateId, releaseId, deploy), Ct);
        return (result, queue);
    }

    private static async Task<TenantProvisionResult> ProvisionAsync(
        AuthTestHost host, int templateId, string slug) {
        await using var scope = host.Services.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();
        return await provisioning.ProvisionAsync(templateId, slug, null, Ct);
    }

    private static async Task SetTemplateDefaultAsync(AuthTestHost host, int templateId, int? releaseId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.StackTemplates.Where(t => t.Id == templateId)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.DefaultPinnedReleaseId, releaseId), Ct);
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
