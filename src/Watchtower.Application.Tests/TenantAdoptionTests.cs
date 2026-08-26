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
/// Covers <c>templates.adoptStack</c>: an existing standalone stack becomes a tenant while keeping its
/// containers, volumes, data, name and compose project.
/// </summary>
/// <remarks>
/// <para>
/// The suite is built around the <b>keep-contract</b>. Every refusal is exercised on its own because each
/// one protects something specific, but the load-bearing test is the happy path, which asserts on what
/// did <em>not</em> change — name, compose project, environment values, pin, backup directory, no deploy.
/// A mutation that copies the template's environment over the stack's, or that steals the primary route,
/// fails it.
/// </para>
/// <para>
/// The handler is constructed directly, like every other tenancy suite: what is asserted is what was
/// written and what was refused, never what a proxy or a deploy then does.
/// </para>
/// </remarks>
public sealed class TenantAdoptionTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The proxy is substituted at <see cref="IProxyProvider"/>, the seam the handler injects, so the
    /// assertion stays "the proxy was asked to reconcile" rather than "this backend was called".
    /// </summary>
    private static readonly Action<IServiceCollection> WithRecordingProxy = services => {
        services.RemoveAll<IProxyProvider>();
        services.AddSingleton<IProxyProvider, RecordingProxyProvider>();
    };

    // ── the keep-contract ────────────────────────────────────────────────────

    /// <summary>
    /// The whole contract in one test: the stack becomes a tenant and <em>nothing it was running
    /// changes</em>. Its name, compose project, environment values, pin and backup directory survive
    /// untouched; it gains a template link, a slug, the base env keys it did not have, and a managed
    /// route that is primary because it had none.
    /// </summary>
    /// <remarks>
    /// Mutation-checked twice, which is what this test exists for. Copying the template's base env over
    /// the stack's (the natural "merge" reading of <see cref="Watchtower.Application.Modules.Tenancy.TenancyMapping.MergeEnv"/>,
    /// which is right for provisioning and wrong here) flips <c>SHARED</c> from <c>stack</c> to
    /// <c>fleet</c> and fails. Renaming the stack or its compose project to <c>{template}-{slug}</c> fails
    /// on the two identity assertions.
    /// </remarks>
    [Fact]
    public async Task Adopt_MakesTheStackATenant_AndChangesNothingItWasRunning() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var v2 = await host.AddReleaseAsync(productId, "v2");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId, defaultPinnedReleaseId: v2);
        await SetBaseEnvAsync(host, templateId, ("SHARED", "fleet"), ("FLEET_ONLY", "fleet"));

        var stackId = await host.AddProductStackAsync("legacy-acme", productId, pinnedReleaseId: v1);
        await SetStackEnvAsync(host, stackId, ("SHARED", "stack"), ("STACK_ONLY", "stack"));
        await StampBackupDirectoryAsync(host, stackId, "wt-1/legacy-acme");

        var result = await AdoptAsync(host, templateId, stackId, "acme");

        Assert.True(result.IsSuccess, Describe(result));
        var response = result.Value;
        Assert.Equal("acme", response.Tenant.TenantSlug);
        Assert.Equal("acme.example.com", response.Domain);
        Assert.True(response.DomainIsPrimary);
        Assert.Equal(["FLEET_ONLY"], response.EnvKeysAdded);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = await db.Stacks.AsNoTracking().SingleAsync(s => s.Id == stackId, Ct);

        // Gained.
        Assert.Equal(templateId, stack.TemplateId);
        Assert.Equal("acme", stack.TenantSlug);

        // Kept — the whole point of the feature.
        Assert.Equal("legacy-acme", stack.Name);
        Assert.Equal("legacy-acme", stack.ComposeProjectName);
        Assert.Equal("wt-1/legacy-acme", stack.BackupDirectory);
        // The fleet default is v2 and it is *not* applied: DefaultPinnedReleaseId is documented as the
        // pin every **future** tenant takes (invariant 17), and an adopted stack is a running one.
        Assert.Equal(v1, stack.PinnedReleaseId);

        // The stack's own value wins by key; the template contributes only what was missing.
        var env = await db.StackEnvVars.AsNoTracking()
            .Where(v => v.StackId == stackId)
            .ToDictionaryAsync(v => v.Key, v => v.Value, Ct);
        Assert.Equal("stack", env["SHARED"]);
        Assert.Equal("stack", env["STACK_ONLY"]);
        Assert.Equal("fleet", env["FLEET_ONLY"]);
        Assert.Equal(3, env.Count);

        var route = await db.Routes.AsNoTracking().SingleAsync(r => r.StackId == stackId, Ct);
        Assert.Equal("acme.example.com", route.Domain);
        Assert.Equal("web", route.ServiceName);
        Assert.Equal(8080, route.ContainerPort);
        Assert.True(route.TlsEnabled);
        Assert.True(route.IsPrimary);
        Assert.Equal(DomainKind.Managed, route.Kind);

        // Nothing about what the stack runs changed, so nothing was redeployed.
        Assert.False(await db.DeployEvents.AnyAsync(Ct));

        var audit = Assert.Single(await AuditAsync(host, AdoptStack.AuditAction));
        Assert.Equal(StackLifecycle.AuditCategory, audit.Category);
        Assert.Equal("legacy-acme", audit.Target);
        Assert.Contains("shop-tenants", audit.Detail!, StringComparison.Ordinal);
        Assert.Contains("acme", audit.Detail!, StringComparison.Ordinal);
        Assert.Contains("acme.example.com", audit.Detail!, StringComparison.Ordinal);
        Assert.Contains("1 env var(s) added", audit.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The proxy is asked to reconcile after the commit — the route exists but nothing serves it until
    /// the target container is on the edge network and the configuration is regenerated.
    /// </summary>
    [Fact]
    public async Task Adopt_ConnectsTheStackAndReloadsTheProxy() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);

        Assert.True((await AdoptAsync(host, templateId, stackId, "acme")).IsSuccess);

        var proxy = (RecordingProxyProvider)host.Services.GetRequiredService<IProxyProvider>();
        Assert.Equal([stackId], proxy.ConnectedStacks);
        Assert.Equal(1, proxy.ApplyCount);
    }

    // ── the route rule ───────────────────────────────────────────────────────

    /// <summary>
    /// A stack already serving a customer-owned domain keeps it as its canonical one. The managed
    /// subdomain is created beside it, not over it — demoting a domain that is on every link, bookmark
    /// and certificate would be a redirect nobody asked for.
    /// </summary>
    /// <remarks>
    /// Mutation-checked by hard-coding <c>IsPrimary = true</c> as provisioning does: both the existing
    /// route's assertion and the response's <c>DomainIsPrimary</c> fail.
    /// </remarks>
    [Fact]
    public async Task Adopt_DoesNotStealPrimaryFromAnExistingCustomDomain() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);
        await host.AddRouteAsync(stackId, "app.acme.test", isPrimary: true, kind: DomainKind.Custom);

        var result = await AdoptAsync(host, templateId, stackId, "acme");

        Assert.True(result.IsSuccess, Describe(result));
        Assert.False(result.Value.DomainIsPrimary);
        Assert.Equal("acme.example.com", result.Value.Domain);
        // The roster row names the stack's canonical domain, which is still the customer's.
        Assert.Equal("app.acme.test", result.Value.Tenant.Domain);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var routes = await db.Routes.AsNoTracking()
            .Where(r => r.StackId == stackId).OrderBy(r => r.Domain)
            .Select(r => new { r.Domain, r.IsPrimary }).ToListAsync(Ct);
        Assert.Equal([("acme.example.com", false), ("app.acme.test", true)],
            routes.Select(r => (r.Domain, r.IsPrimary)));
    }

    /// <summary>A stack whose only route is non-primary still gets a primary — there is none to steal.</summary>
    [Fact]
    public async Task Adopt_TakesPrimaryWhenTheStacksExistingRouteIsNot() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);
        await host.AddRouteAsync(stackId, "alias.acme.test", isPrimary: false);

        var result = await AdoptAsync(host, templateId, stackId, "acme");

        Assert.True(result.IsSuccess, Describe(result));
        Assert.True(result.Value.DomainIsPrimary);
        Assert.Equal("acme.example.com", result.Value.Tenant.Domain);
    }

    // ── the branch is preserved ──────────────────────────────────────────────

    /// <summary>
    /// A tenant inherits its template's branch override, so a stack with none of its own would start
    /// deploying the fleet's branch the moment it is adopted. The branch it was actually deploying is
    /// written onto it instead, because adoption may not change what the stack runs.
    /// </summary>
    /// <remarks>
    /// Mutation-checked by dropping the <c>BranchOverride</c> write: the stack ends up inheriting
    /// <c>develop</c> and the assertion on the effective branch fails.
    /// </remarks>
    [Fact]
    public async Task Adopt_KeepsTheBranchTheStackWasDeploying_WhenTheSetupWouldHaveMovedIt() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetTemplateBranchAsync(host, templateId, "develop");
        // No override of its own: it deploys the product default, "main".
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);

        Assert.True((await AdoptAsync(host, templateId, stackId, "acme")).IsSuccess);

        Assert.Equal(TestProducts.DefaultBranch, await EffectiveBranchAsync(host, stackId));
        Assert.Equal(TestProducts.DefaultBranch, await BranchOverrideAsync(host, stackId));
    }

    /// <summary>
    /// And it does not pin what it would inherit anyway (invariant 5): a stack whose branch already
    /// agrees with the setup keeps <c>BranchOverride</c> null, so a later fleet-wide branch change still
    /// reaches it.
    /// </summary>
    [Fact]
    public async Task Adopt_LeavesTheBranchInheriting_WhenTheSetupAgreesWithIt() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetTemplateBranchAsync(host, templateId, "develop");
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);
        await SetStackBranchAsync(host, stackId, "develop");

        Assert.True((await AdoptAsync(host, templateId, stackId, "acme")).IsSuccess);

        Assert.Null(await BranchOverrideAsync(host, stackId));
        Assert.Equal("develop", await EffectiveBranchAsync(host, stackId));
    }

    // ── refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Adopt_RefusesAnUnknownTemplate() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);

        var result = await AdoptAsync(host, 404, stackId, "acme");

        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
        await AssertStandaloneAsync(host, stackId);
    }

    [Fact]
    public async Task Adopt_RefusesAnUnknownStack() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);

        var result = await AdoptAsync(host, templateId, 404, "acme");

        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    /// <summary>A stack belongs to one setup at a time, and the refusal names the one it is in.</summary>
    [Fact]
    public async Task Adopt_RefusesAStackThatIsAlreadyATenant() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var incumbent = await host.AddProductTemplateAsync("shop-tenants", productId);
        var other = await host.AddProductTemplateAsync("shop-eu", productId);
        var stackId = await host.AddProductStackAsync(
            "shop-globex", productId, templateId: incumbent, tenantSlug: "globex");

        var result = await AdoptAsync(host, other, stackId, "acme");

        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Contains("shop-tenants", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("globex", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Adoption never re-points a stack at another codebase — the <c>templates.update</c> product-change
    /// refusal, one rung down. Both products are named, because the caller's mistake is which of the two
    /// they picked.
    /// </summary>
    [Fact]
    public async Task Adopt_RefusesAStackOfAnotherProduct() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var shop = await host.AddProductAsync("shop");
        var blog = await host.AddProductAsync("blog");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", shop);
        var stackId = await host.AddProductStackAsync("blog-prod", blog);

        var result = await AdoptAsync(host, templateId, stackId, "acme");

        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Contains("blog", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("shop", result.Error.Message, StringComparison.Ordinal);
        await AssertStandaloneAsync(host, stackId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-leading-hyphen")]
    [InlineData("has spaces")]
    [InlineData("has.dots")]
    [InlineData("has_underscore")]
    public async Task Adopt_RefusesAnUnusableSlug(string slug) {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);

        var result = await AdoptAsync(host, templateId, stackId, slug);

        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        await AssertStandaloneAsync(host, stackId);
    }

    /// <summary>The management API's literal path segment, refused on the normalized value as provisioning does.</summary>
    [Theory]
    [InlineData("accessible")]
    [InlineData("  Accessible  ")]
    public async Task Adopt_RefusesAReservedSlug(string slug) {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);

        var result = await AdoptAsync(host, templateId, stackId, slug);

        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Equal("Slug 'accessible' is reserved.", result.Error.Message);
        await AssertStandaloneAsync(host, stackId);
    }

    /// <summary>The slug is taken, and the refusal names the stack holding it.</summary>
    [Fact]
    public async Task Adopt_RefusesASlugTheSetupAlreadyHas() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await host.AddProductStackAsync("shop-tenants-acme", productId, templateId: templateId, tenantSlug: "acme");
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);

        var result = await AdoptAsync(host, templateId, stackId, "acme");

        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Contains("shop-tenants-acme", result.Error.Message, StringComparison.Ordinal);
        await AssertStandaloneAsync(host, stackId);
    }

    /// <summary>
    /// Domains are globally unique, so a rendered domain that exists is refused rather than moved — and
    /// the refusal names the route's owner, which is the only thing the caller can act on.
    /// </summary>
    [Fact]
    public async Task Adopt_RefusesADomainAlreadyRouted() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var squatter = await host.AddProductStackAsync("someone-else", productId);
        await host.AddRouteAsync(squatter, "acme.example.com");
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);

        var result = await AdoptAsync(host, templateId, stackId, "acme");

        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Contains("acme.example.com", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("someone-else", result.Error.Message, StringComparison.Ordinal);
        await AssertStandaloneAsync(host, stackId);
    }

    /// <summary>
    /// The keep-contract's null side. The happy path above proves an existing pin is not overwritten by
    /// the fleet default; this proves the *absence* of one is not filled in with it either. Both
    /// directions matter, and only together do they say "the pin is untouched": a mutation that reads
    /// <see cref="StackTemplate.DefaultPinnedReleaseId"/> the way provisioning does — a stack with no pin
    /// is exactly the row it would land on — leaves the other test green.
    /// </summary>
    /// <remarks>
    /// Mutation-checked by copying the default onto the stack
    /// (<c>stack.PinnedReleaseId ??= template.DefaultPinnedReleaseId</c>), which is provisioning's line
    /// verbatim: this fails and nothing else does.
    /// </remarks>
    [Fact]
    public async Task Adopt_LeavesAnUnpinnedStackTrackingLatest_EvenUnderAFleetDefault() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var v1 = await host.AddReleaseAsync(productId, "v1");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId, defaultPinnedReleaseId: v1);
        // No pin of its own: it tracks latest, and adoption is not a provisioning.
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);

        var result = await AdoptAsync(host, templateId, stackId, "acme");

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Null((await host.ReleaseStateAsync(stackId)).Pinned);
        Assert.Equal(TenancyMapping.TrackingLatest, result.Value.Tenant.TrackingMode);
        Assert.Null(result.Value.Tenant.PinnedRelease);
        // And the fleet default is still the fleet's — adoption wrote neither half.
        Assert.Equal(v1, await host.TemplateDefaultAsync(templateId));
    }

    // ── the realm rule ───────────────────────────────────────────────────────

    /// <summary>
    /// A service route takes its realm from its stack's category, so adopting into a non-system setup
    /// would re-point every protected route the stack already serves at another population — the accounts
    /// using them today would stop being admitted, and the new realm's would be let in ungranted. That is
    /// what <c>templates.update</c> refuses on a populated template, and it is refused here for the same
    /// reason, naming the routes and the way through.
    /// </summary>
    /// <remarks>
    /// Mutation-checked by dropping the pre-flight: the adoption succeeds and the protected routes
    /// silently change hands, which is the whole defect.
    /// </remarks>
    [Fact]
    public async Task Adopt_IntoANonSystemRealm_RefusesAStackWithProtectedDomains() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var realmId = await AddRealmAsync(host, "Customers", "customers");
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetTemplateRealmAsync(host, templateId, realmId);
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);
        await host.AddRouteAsync(stackId, "portal.acme.test", isPrimary: true, kind: DomainKind.Custom);
        await host.AddRouteAsync(stackId, "admin.acme.test", isPrimary: false);
        await ProtectRouteAsync(host, "portal.acme.test");
        await ProtectRouteAsync(host, "admin.acme.test");

        var result = await AdoptAsync(host, templateId, stackId, "acme");

        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        // The routes by name — the operator has to know which ones to unprotect — and the realm they
        // would have moved to, which is named twice: once as the cause and once as the way through.
        Assert.Contains("portal.acme.test", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("admin.acme.test", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("Customers", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("2 protected domain(s)", result.Error.Message, StringComparison.Ordinal);
        await AssertStandaloneAsync(host, stackId);
    }

    /// <summary>
    /// The other branch, and the one that keeps the feature usable: <see cref="AccessMode.Public"/>
    /// admits everyone in every realm, so a stack whose domains are all public moves no population and
    /// adopts freely. The dialog states the realm it is joining; the backend does not stand in the way.
    /// </summary>
    [Fact]
    public async Task Adopt_IntoANonSystemRealm_IsAllowedWhenEveryDomainIsPublic() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var realmId = await AddRealmAsync(host, "Customers", "customers");
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetTemplateRealmAsync(host, templateId, realmId);
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);
        await host.AddRouteAsync(stackId, "portal.acme.test", isPrimary: true, kind: DomainKind.Custom);

        var result = await AdoptAsync(host, templateId, stackId, "acme");

        Assert.True(result.IsSuccess, Describe(result));
        Assert.False(result.Value.DomainIsPrimary);
    }

    /// <summary>
    /// And a *system*-realm setup never asks the question: a standalone stack's routes already belong to
    /// the system realm, so nothing moves however protected they are. Without this clause the refusal
    /// would block the ordinary single-realm install, which is every install that has not configured
    /// realms at all.
    /// </summary>
    [Fact]
    public async Task Adopt_IntoTheSystemRealm_AllowsAProtectedStack() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);
        await host.AddRouteAsync(stackId, "portal.acme.test", isPrimary: true, kind: DomainKind.Custom);
        await ProtectRouteAsync(host, "portal.acme.test");

        Assert.True((await AdoptAsync(host, templateId, stackId, "acme")).IsSuccess);
    }

    /// <summary>
    /// Every template read projects the realm's <em>name</em>, which the adoption dialog states without
    /// being able to call the Admin-only <c>realms.list</c>. It is a navigation, so a handler that forgets
    /// the <c>Include</c> answers "no realm" over a template that has one — the trap two read paths fell
    /// into with the fleet default before that had a test of its own.
    /// </summary>
    /// <remarks>
    /// It lives in this suite because adoption is what put the field on the wire, and it covers
    /// <c>create</c> too — which the fleet-default test does not, and which is the path most likely to
    /// answer from a freshly constructed entity rather than from a re-read.
    /// </remarks>
    [Fact]
    public async Task TemplateReads_AllProjectTheRealmName() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var realmId = await AddRealmAsync(host, "Customers", "customers");
        var productId = await host.AddProductAsync("shop");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        var create = await ActivatorUtilities.CreateInstance<CreateTemplate>(scope.ServiceProvider)
            .HandleAsync(
                new CreateTemplate.Command(
                    "shop-tenants", RepositoryUrl: "", ComposeFilePath: "", Branch: "", CredentialId: null,
                    DomainPattern: "{tenant}.example.com", TargetServiceName: "web", TargetPort: 8080,
                    BaseEnvVars: null, RealmId: realmId, ProductId: productId),
                Ct);
        Assert.True(create.IsSuccess, Describe(create));
        var templateId = create.Value.Template.Id;

        var get = await ActivatorUtilities.CreateInstance<GetTemplate>(scope.ServiceProvider)
            .HandleAsync(new GetTemplate.Query(templateId), Ct);
        var list = await ActivatorUtilities.CreateInstance<ListTemplates>(scope.ServiceProvider)
            .HandleAsync(new ListTemplates.Query(), Ct);
        var row = await db.StackTemplates.AsNoTracking().FirstAsync(t => t.Id == templateId, Ct);
        var update = await ActivatorUtilities.CreateInstance<UpdateTemplate>(scope.ServiceProvider)
            .HandleAsync(
                new UpdateTemplate.Command(
                    templateId, row.Name,
                    // Read-only projections since ADR-0026; posting the effective values back is what the
                    // frontend does and what update compares against.
                    RepositoryUrl: "https://example.invalid/shop.git",
                    ComposeFilePath: TestProducts.ComposeFilePath,
                    Branch: TestProducts.DefaultBranch,
                    CredentialId: null,
                    row.DomainPattern, row.TargetServiceName, row.TargetPort, BaseEnvVars: null),
                Ct);

        Assert.Equal("Customers", create.Value.Template.RealmName);
        Assert.Equal("Customers", get.Value.Template.RealmName);
        Assert.Equal("Customers", Assert.Single(list.Value.Templates).RealmName);
        Assert.Equal("Customers", update.Value.Template.RealmName);
    }

    // ── atomicity ────────────────────────────────────────────────────────────

    /// <summary>
    /// The template link, the env rows and the route are one write: a route insert that loses the unique
    /// domain index must leave the stack standalone, not half-adopted with no way in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The failure is forced without a production seam, the <c>SetTenantsRelease</c> pattern: a duplicate
    /// route is staged on the <em>same scoped change tracker</em> the handler resolves, so the handler's
    /// own <c>SaveChangesAsync</c> flushes both inserts into the unique index. The pre-flight domain check
    /// queries the database and cannot see it, which is exactly the concurrent-writer shape it stands in
    /// for.
    /// </para>
    /// <para>
    /// Mutation-checked by splitting the write in two (link first, then env and route): the link commits,
    /// the route fails, and the first assertion catches a stack that is a tenant of a setup it has no
    /// domain in.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Adopt_ThatCannotWriteItsRoute_LeavesTheStackStandalone() {
        using var host = AuthTestHost.Start(WithRecordingProxy);
        var productId = await host.AddProductAsync("shop");
        var templateId = await host.AddProductTemplateAsync("shop-tenants", productId);
        await SetBaseEnvAsync(host, templateId, ("FLEET_ONLY", "fleet"));
        var stackId = await host.AddProductStackAsync("legacy-acme", productId);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            // Staged, not saved: the handler shares this context, so its save flushes both.
            db.Routes.Add(new Route {
                StackId = stackId,
                Domain = "acme.example.com",
                ServiceName = "web",
                ContainerPort = 8080,
                Kind = DomainKind.Managed,
                CreatedAt = DateTimeOffset.UtcNow,
            });

            var handler = ActivatorUtilities.CreateInstance<AdoptStack>(scope.ServiceProvider);
            var result = await handler.HandleAsync(new AdoptStack.Command(templateId, stackId, "acme"), Ct);
            Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        }

        await AssertStandaloneAsync(host, stackId);
        await using var verify = host.Services.CreateAsyncScope();
        var check = verify.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await check.Routes.AnyAsync(r => r.StackId == stackId, Ct));
        Assert.False(await check.StackEnvVars.AnyAsync(v => v.StackId == stackId, Ct));
        // The proxy is only ever asked after the commit point, so a failed adoption asks it nothing.
        var proxy = (RecordingProxyProvider)host.Services.GetRequiredService<IProxyProvider>();
        Assert.Empty(proxy.ConnectedStacks);
        Assert.Equal(0, proxy.ApplyCount);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<Result<AdoptStack.Response>> AdoptAsync(
        AuthTestHost host, int templateId, int stackId, string slug) {
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<AdoptStack>(scope.ServiceProvider);
        return await handler.HandleAsync(new AdoptStack.Command(templateId, stackId, slug), Ct);
    }

    private static async Task SetBaseEnvAsync(
        AuthTestHost host, int templateId, params (string Key, string Value)[] vars) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        foreach (var (key, value) in vars)
            db.StackTemplateEnvVars.Add(new StackTemplateEnvVar { TemplateId = templateId, Key = key, Value = value });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task SetStackEnvAsync(
        AuthTestHost host, int stackId, params (string Key, string Value)[] vars) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        foreach (var (key, value) in vars)
            db.StackEnvVars.Add(new StackEnvVar { StackId = stackId, Key = key, Value = value });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task StampBackupDirectoryAsync(AuthTestHost host, int stackId, string directory) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Stacks.Where(s => s.Id == stackId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BackupDirectory, directory), Ct);
    }

    private static async Task SetTemplateBranchAsync(AuthTestHost host, int templateId, string? branch) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.StackTemplates.Where(t => t.Id == templateId)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.BranchOverride, branch), Ct);
    }

    private static async Task SetStackBranchAsync(AuthTestHost host, int stackId, string? branch) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Stacks.Where(s => s.Id == stackId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BranchOverride, branch), Ct);
    }

    /// <summary>Adds a second population, which is what makes the realm rule observable at all.</summary>
    private static async Task<int> AddRealmAsync(AuthTestHost host, string name, string slug) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var realm = new Realm { Name = name, Slug = slug, CreatedAt = DateTimeOffset.UtcNow };
        db.Realms.Add(realm);
        await db.SaveChangesAsync(Ct);
        return realm.Id;
    }

    private static async Task SetTemplateRealmAsync(AuthTestHost host, int templateId, int realmId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.StackTemplates.Where(t => t.Id == templateId)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.RealmId, realmId), Ct);
    }

    /// <summary>Takes a route off <see cref="AccessMode.Public"/> — the state the realm rule turns on.</summary>
    private static async Task ProtectRouteAsync(AuthTestHost host, string domain) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Routes.Where(r => r.Domain == domain)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.AccessMode, AccessMode.Authenticated), Ct);
    }

    private static async Task<string?> BranchOverrideAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Stacks.AsNoTracking()
            .Where(s => s.Id == stackId).Select(s => s.BranchOverride).FirstAsync(Ct);
    }

    /// <summary>What the deploy pipeline would actually clone, read through the one resolver.</summary>
    private static async Task<string> EffectiveBranchAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = await db.Stacks.AsNoTracking()
            .Include(s => s.Product).Include(s => s.Template)
            .FirstAsync(s => s.Id == stackId, Ct);
        return ProductSourceResolver.Resolve(stack).Branch;
    }

    private static async Task AssertStandaloneAsync(AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == stackId)
            .Select(s => new { s.TemplateId, s.TenantSlug })
            .FirstAsync(Ct);
        Assert.Null(stack.TemplateId);
        Assert.Null(stack.TenantSlug);
    }

    private static async Task<List<AuditEvent>> AuditAsync(AuthTestHost host, string action) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == action).OrderBy(e => e.Id).ToListAsync(Ct);
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
