using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Products.Handlers;
using Watchtower.Application.Modules.Stacks;
using Watchtower.Application.Modules.Stacks.Handlers;
using Watchtower.Application.Modules.Tenancy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The RPC surface of ADR-0026: the implicit-product contract behind <c>stacks.create</c>, the
/// back-compat rules <c>stacks.update</c> and <c>templates.update</c> apply to the repository fields
/// that are now read-only projections, and <c>products.*</c> itself.
/// </summary>
/// <remarks>
/// The back-compat contract is what most of this pins: an unchanged repository field must pass
/// silently, because the existing UI loads a stack and posts the whole object back. A rule that
/// rejected on presence rather than on change would break every save in the product.
/// </remarks>
public sealed class ProductsRpcTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Repo = "https://github.com/acme/shop.git";
    private const string Compose = "docker-compose.yml";

    /// <summary>What every product in this suite defaults to, so an override is visibly not it.</summary>
    private const string DefaultBranch = "main";

    // ── stacks.create ────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_FindsOrCreatesTheProductBehindTheInlineRepositoryFields() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();

        var first = await CreateStackAsync(scope.ServiceProvider, "shop", Repo, Compose, "main");
        Assert.True(first.IsSuccess, Describe(first));

        // A second stack over the same source — spelled differently — joins the same product rather
        // than forking a near-duplicate.
        var second = await CreateStackAsync(
            scope.ServiceProvider, "shop-staging", "https://GitHub.com/acme/shop", "/docker-compose.yml", "main");
        Assert.True(second.IsSuccess, Describe(second));

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var product = await db.Products.AsNoTracking().SingleAsync(Ct);
        Assert.Equal("shop", product.Name);
        Assert.Equal(product.Id, first.Value.Stack.ProductId);
        Assert.Equal(product.Id, second.Value.Stack.ProductId);

        // The DTO still answers the four source questions, from the product.
        Assert.Equal(Repo, first.Value.Stack.RepositoryUrl);
        Assert.Equal(Compose, first.Value.Stack.ComposeFilePath);
        Assert.Equal("main", first.Value.Stack.Branch);
        Assert.Equal("shop", first.Value.Stack.ProductName);

        // A product nobody asked for by name is still a product an operator will find later.
        var audit = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Category == "products" && e.Action == "product.create", Ct);
        Assert.Equal("shop", audit.Target);
        Assert.Contains("implicit via stacks.create", audit.Detail);
    }

    /// <summary>A branch that is not the found product's default becomes a per-stack override.</summary>
    [Fact]
    public async Task Create_TurnsADivergentBranchIntoAnOverrideRatherThanASecondProduct() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();

        await CreateStackAsync(scope.ServiceProvider, "shop", Repo, Compose, "main");
        var staging = await CreateStackAsync(scope.ServiceProvider, "shop-staging", Repo, Compose, "develop");

        Assert.True(staging.IsSuccess, Describe(staging));
        Assert.Equal("develop", staging.Value.Stack.BranchOverride);
        Assert.Equal("develop", staging.Value.Stack.Branch);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        // The product keeps its own default: one stack's branch is not the fleet's.
        Assert.Equal("main", (await db.Products.AsNoTracking().SingleAsync(Ct)).DefaultBranch);
    }

    [Fact]
    public async Task Create_AcceptsAnExistingProductId_AndRefusesItAlongsideTheRepositoryFields() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var productId = await AddProductAsync(scope.ServiceProvider, "shop", Repo, "main");

        var byId = await CreateStackAsync(scope.ServiceProvider, "shop", "", "", "", productId);
        Assert.True(byId.IsSuccess, Describe(byId));
        Assert.Equal(productId, byId.Value.Stack.ProductId);
        Assert.Equal(Repo, byId.Value.Stack.RepositoryUrl);
        Assert.Null(byId.Value.Stack.BranchOverride);

        var both = await CreateStackAsync(scope.ServiceProvider, "other", Repo, Compose, "main", productId);
        Assert.False(both.IsSuccess);
        Assert.Equal(ErrorKind.Validation, both.Error.Kind);
        Assert.Contains("not both", both.Error.Message, StringComparison.Ordinal);
    }

    // ── stacks.update ────────────────────────────────────────────────────────

    /// <summary>
    /// The back-compat contract: the UI posts back exactly what it loaded, and that has to keep working.
    /// </summary>
    [Fact]
    public async Task Update_AcceptsTheRepositoryFieldsItHandedOutUnchanged() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var created = (await CreateStackAsync(scope.ServiceProvider, "shop", Repo, Compose, "main")).Value.Stack;

        var saved = await UpdateStackAsync(
            scope.ServiceProvider, created.Id, "shop-renamed",
            created.RepositoryUrl, created.ComposeFilePath, created.Branch, created.CredentialId);

        Assert.True(saved.IsSuccess, Describe(saved));
        Assert.Equal("shop-renamed", saved.Value.Stack.Name);
        Assert.Equal(Repo, saved.Value.Stack.RepositoryUrl);
        Assert.Null(saved.Value.Stack.BranchOverride);
    }

    [Theory]
    [InlineData("https://github.com/acme/other.git", Compose, "repository URL")]
    [InlineData(Repo, "deploy/compose.yaml", "compose file path")]
    public async Task Update_RefusesAChangedSourceFieldAndPointsAtProductsUpdate(
        string repositoryUrl, string composeFilePath, string expected) {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var created = (await CreateStackAsync(scope.ServiceProvider, "shop", Repo, Compose, "main")).Value.Stack;

        var refused = await UpdateStackAsync(
            scope.ServiceProvider, created.Id, "shop", repositoryUrl, composeFilePath, "main", null);

        Assert.False(refused.IsSuccess);
        Assert.Equal(ErrorKind.Validation, refused.Error.Kind);
        Assert.Contains(expected, refused.Error.Message, StringComparison.Ordinal);
        Assert.Contains("products.update", refused.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>Branch is the exception — it still moves one stack, via the override.</summary>
    [Fact]
    public async Task Update_MapsTheBranchOntoTheOverrideAndClearsItAtTheProductDefault() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var created = (await CreateStackAsync(scope.ServiceProvider, "shop", Repo, Compose, "main")).Value.Stack;

        var pinned = await UpdateStackAsync(scope.ServiceProvider, created.Id, "shop", Repo, Compose, "develop", null);
        Assert.True(pinned.IsSuccess, Describe(pinned));
        Assert.Equal("develop", pinned.Value.Stack.BranchOverride);
        Assert.Equal("develop", pinned.Value.Stack.Branch);

        var cleared = await UpdateStackAsync(scope.ServiceProvider, created.Id, "shop", Repo, Compose, "main", null);
        Assert.True(cleared.IsSuccess, Describe(cleared));
        Assert.Null(cleared.Value.Stack.BranchOverride);
        Assert.Equal("main", cleared.Value.Stack.Branch);
    }

    // ── products.* ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RefusesWhileAnythingStillDeploysTheProduct_AndNamesIt() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var stack = (await CreateStackAsync(scope.ServiceProvider, "shop", Repo, Compose, "main")).Value.Stack;

        var delete = ActivatorUtilities.CreateInstance<DeleteProduct>(scope.ServiceProvider);
        var refused = await delete.HandleAsync(new DeleteProduct.Command(stack.ProductId), Ct);

        Assert.False(refused.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, refused.Error.Kind);
        Assert.Contains("1 stack(s) (shop)", refused.Error.Message, StringComparison.Ordinal);

        // Free it, and the same call goes through. A second scope, because the first one still tracks
        // the stack this deletes out from under it.
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Stacks.Where(s => s.Id == stack.Id).ExecuteDeleteAsync(Ct);

        await using var after = host.Services.CreateAsyncScope();
        var retried = ActivatorUtilities.CreateInstance<DeleteProduct>(after.ServiceProvider);
        Assert.True((await retried.HandleAsync(new DeleteProduct.Command(stack.ProductId), Ct)).IsSuccess);
        Assert.Empty(await after.ServiceProvider.GetRequiredService<WatchtowerDbContext>()
            .Products.AsNoTracking().ToListAsync(Ct));
    }

    /// <summary>
    /// A repository move is legal and reaches every stack of the product — which is why the audit row
    /// has to say so in words, with the blast radius counted.
    /// </summary>
    [Fact]
    public async Task Update_AuditsARepositoryMoveLoudlyWithTheNumberOfDeploymentsItReaches() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var stack = (await CreateStackAsync(scope.ServiceProvider, "shop", Repo, Compose, "main")).Value.Stack;

        var update = ActivatorUtilities.CreateInstance<UpdateProduct>(scope.ServiceProvider);
        var moved = await update.HandleAsync(
            new UpdateProduct.Command(stack.ProductId, "shop", "https://gitlab.com/acme/shop.git", Compose, "main"),
            Ct);

        Assert.True(moved.IsSuccess, Describe(moved));
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var audit = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Category == "products" && e.Action == "product.update", Ct);
        Assert.Contains("REPOSITORY CHANGED", audit.Detail, StringComparison.Ordinal);
        Assert.Contains("https://gitlab.com/acme/shop.git", audit.Detail, StringComparison.Ordinal);
        Assert.Contains("1 stack(s)", audit.Detail, StringComparison.Ordinal);

        // …and the stack, which stored nothing of its own, is already on the new source.
        var get = ActivatorUtilities.CreateInstance<GetStack>(scope.ServiceProvider);
        var reread = await get.HandleAsync(new GetStack.Query(stack.Id), Ct);
        Assert.Equal("https://gitlab.com/acme/shop.git", reread.Value.Stack.RepositoryUrl);
    }

    /// <summary>
    /// The catalogue and the detail page, executed against the database rather than reasoned about:
    /// both do their counting and their branch resolution in SQL, which is exactly the kind of thing a
    /// compile cannot check.
    /// </summary>
    [Fact]
    public async Task ListAndGet_CountAndResolveEverythingTheRostersShow() {
        using var host = AuthTestHost.Start();
        var templateId = await CreateTemplateAsync(host, "shop");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var productId = (await db.StackTemplates.AsNoTracking().SingleAsync(Ct)).ProductId;

        var provisioning = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();
        Assert.Equal(TenantProvisionStatus.Created, (await provisioning.ProvisionAsync(templateId, "acme", null, Ct)).Status);
        await CreateStackAsync(scope.ServiceProvider, "shop-staging", Repo, Compose, "develop");

        var list = await ActivatorUtilities.CreateInstance<ListProducts>(scope.ServiceProvider)
            .HandleAsync(new ListProducts.Query(), Ct);
        Assert.True(list.IsSuccess, Describe(list));
        var listed = Assert.Single(list.Value.Products);
        Assert.Equal(2, listed.StackCount);
        Assert.Equal(1, listed.TemplateCount);

        var detail = await ActivatorUtilities.CreateInstance<GetProduct>(scope.ServiceProvider)
            .HandleAsync(new GetProduct.Query(productId), Ct);
        Assert.True(detail.IsSuccess, Describe(detail));
        Assert.Equal(2, detail.Value.Stacks.Count);

        var tenant = detail.Value.Stacks.Single(s => s.TenantSlug == "acme");
        Assert.Equal("main", tenant.Branch);
        Assert.Null(tenant.BranchOverride);
        Assert.Equal(templateId, tenant.TemplateId);

        var staging = detail.Value.Stacks.Single(s => s.Name == "shop-staging");
        Assert.Equal("develop", staging.Branch);
        Assert.Equal("develop", staging.BranchOverride);

        var template = Assert.Single(detail.Value.Templates);
        Assert.Equal("main", template.Branch);
        Assert.Equal(1, template.TenantCount);
    }

    // ── tenancy ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The bug ADR-0026 removes by construction: a template's source edit used to stop at the tenants
    /// it had already stamped. Nothing is copied now, so it reaches them.
    /// </summary>
    [Fact]
    public async Task ATenantFollowsItsTemplatesProduct_SoASourceEditReachesIt() {
        using var host = AuthTestHost.Start();
        var templateId = await CreateTemplateAsync(host, "shop");
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        var provisioning = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();
        var tenant = await provisioning.ProvisionAsync(templateId, "acme", null, Ct);
        Assert.Equal(TenantProvisionStatus.Created, tenant.Status);

        var template = await db.StackTemplates.AsNoTracking().SingleAsync(Ct);
        var stack = await db.Stacks.AsNoTracking().SingleAsync(s => s.TemplateId == templateId, Ct);
        Assert.Equal(template.ProductId, stack.ProductId);
        Assert.Null(stack.BranchOverride);

        // Move the product; the tenant follows without anything being written to it.
        var update = ActivatorUtilities.CreateInstance<UpdateProduct>(scope.ServiceProvider);
        Assert.True((await update.HandleAsync(
            new UpdateProduct.Command(template.ProductId, "shop", "https://gitlab.com/acme/shop.git", Compose, "main"),
            Ct)).IsSuccess);

        var get = ActivatorUtilities.CreateInstance<GetStack>(scope.ServiceProvider);
        var reread = await get.HandleAsync(new GetStack.Query(stack.Id), Ct);
        Assert.Equal("https://gitlab.com/acme/shop.git", reread.Value.Stack.RepositoryUrl);
    }


    /// <summary>
    /// The regression this rule exists for. A tenant of a <c>develop</c> template inherits
    /// <c>develop</c>; the settings form loads that as the effective branch and posts it back on any
    /// save. Comparing it against the <em>product</em> default would write <c>develop</c> onto the
    /// tenant as a personal pin, and the tenant would then stop following its template forever — the
    /// copy-instead-of-inherit bug ADR-0026 exists to delete, reintroduced by one webhook toggle.
    /// </summary>
    [Fact]
    public async Task Update_DoesNotPinATenantToTheBranchItMerelyInheritsFromItsTemplate() {
        using var host = AuthTestHost.Start();
        var templateId = await CreateTemplateAsync(host, "shop", branch: "develop");
        var tenantId = await AddTenantAsync(host, templateId, "acme");

        // A scope of its own: provisioning enqueues the initial deploy, whose status write bumps the
        // stack's xmin, so a context that had already tracked the row would lose the concurrency check.
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var tenant = await db.Stacks.AsNoTracking().SingleAsync(s => s.Id == tenantId, Ct);

        var loaded = await ActivatorUtilities.CreateInstance<GetStack>(scope.ServiceProvider)
            .HandleAsync(new GetStack.Query(tenant.Id), Ct);
        Assert.Equal("develop", loaded.Value.Stack.Branch);
        Assert.Null(loaded.Value.Stack.BranchOverride);

        // Exactly what the settings form posts: everything it was handed, one unrelated field flipped.
        var saved = await UpdateStackAsync(
            scope.ServiceProvider, tenant.Id, loaded.Value.Stack.Name,
            loaded.Value.Stack.RepositoryUrl, loaded.Value.Stack.ComposeFilePath, loaded.Value.Stack.Branch,
            loaded.Value.Stack.CredentialId, webhookEnabled: true);

        Assert.True(saved.IsSuccess, Describe(saved));
        Assert.Null(saved.Value.Stack.BranchOverride);
        Assert.Equal("develop", saved.Value.Stack.Branch);
        Assert.Null((await db.Stacks.AsNoTracking().SingleAsync(s => s.Id == tenant.Id, Ct)).BranchOverride);

        // …and the template still reaches it: move the template's branch, the tenant follows.
        await db.StackTemplates.Where(x => x.Id == templateId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BranchOverride, "release"), Ct);
        await using var later = host.Services.CreateAsyncScope();
        var followed = await ActivatorUtilities.CreateInstance<GetStack>(later.ServiceProvider)
            .HandleAsync(new GetStack.Query(tenant.Id), Ct);
        Assert.Equal("release", followed.Value.Stack.Branch);
    }

    /// <summary>The other half: a tenant deliberately posting a different branch still gets its pin.</summary>
    [Fact]
    public async Task Update_StillPinsATenantThatDeliberatelyAsksForAnotherBranch() {
        using var host = AuthTestHost.Start();
        var templateId = await CreateTemplateAsync(host, "shop", branch: "develop");
        var tenantId = await AddTenantAsync(host, templateId, "acme");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var tenant = await db.Stacks.AsNoTracking().SingleAsync(s => s.Id == tenantId, Ct);

        var pinned = await UpdateStackAsync(
            scope.ServiceProvider, tenant.Id, tenant.Name, Repo, Compose, "hotfix", null);

        Assert.True(pinned.IsSuccess, Describe(pinned));
        Assert.Equal("hotfix", pinned.Value.Stack.BranchOverride);
        Assert.Equal("hotfix", pinned.Value.Stack.Branch);

        // And posting the product default is a real choice too — it overrides the template's develop.
        var toMain = await UpdateStackAsync(
            scope.ServiceProvider, tenant.Id, tenant.Name, Repo, Compose, "main", null);
        Assert.True(toMain.IsSuccess, Describe(toMain));
        Assert.Equal("main", toMain.Value.Stack.BranchOverride);
    }

    /// <summary>
    /// A credential supplied next to repository fields that match an existing product would otherwise
    /// be dropped silently, leaving a stack cloning with a credential nobody chose for it.
    /// </summary>
    [Fact]
    public async Task Create_RefusesACredentialThatDisagreesWithTheMatchedProduct() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var credential = new Credential { Name = "clone", Username = "git", Token = "t", CreatedAt = DateTimeOffset.UtcNow };
        db.Credentials.Add(credential);
        await db.SaveChangesAsync(Ct);

        // First stack establishes the product with no credential.
        Assert.True((await CreateStackAsync(scope.ServiceProvider, "shop", Repo, Compose, "main")).IsSuccess);

        var refused = await CreateStackAsync(
            scope.ServiceProvider, "shop-two", Repo, Compose, "main", credentialId: credential.Id);
        Assert.False(refused.IsSuccess);
        Assert.Equal(ErrorKind.Validation, refused.Error.Kind);
        Assert.Contains("git credential", refused.Error.Message, StringComparison.Ordinal);
        Assert.Contains("products.update", refused.Error.Message, StringComparison.Ordinal);

        // The matching credential passes, and so does omitting it.
        Assert.True((await CreateStackAsync(scope.ServiceProvider, "shop-three", Repo, Compose, "main")).IsSuccess);
        Assert.Single(await db.Products.AsNoTracking().ToListAsync(Ct));
    }

    /// <summary>
    /// Two products over one normalized source would leave find-or-create picking by id, so every later
    /// stacks.create would silently join the older one.
    /// </summary>
    [Fact]
    public async Task ProductWrites_RefuseASourceAnotherProductAlreadyOwns() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var firstId = await AddProductAsync(scope.ServiceProvider, "shop", Repo, "main");

        var duplicate = await ActivatorUtilities.CreateInstance<CreateProduct>(scope.ServiceProvider)
            // A cosmetically different spelling of the same source is the same source.
            .HandleAsync(new CreateProduct.Command("shop-copy", "https://GitHub.com/acme/shop", Compose, "main"), Ct);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal(ErrorKind.Validation, duplicate.Error.Kind);
        Assert.Contains("'shop' already deploys", duplicate.Error.Message, StringComparison.Ordinal);

        // A second product over the same repository but another compose file is legitimate…
        var sibling = await ActivatorUtilities.CreateInstance<CreateProduct>(scope.ServiceProvider)
            .HandleAsync(new CreateProduct.Command("shop-api", Repo, "apps/api/compose.yaml", "main"), Ct);
        Assert.True(sibling.IsSuccess, Describe(sibling));

        // …until it is edited onto the first one's source.
        var collide = await ActivatorUtilities.CreateInstance<UpdateProduct>(scope.ServiceProvider)
            .HandleAsync(new UpdateProduct.Command(sibling.Value.Product.Id, "shop-api", Repo, Compose, "main"), Ct);
        Assert.False(collide.IsSuccess);
        Assert.Contains("'shop' already deploys", collide.Error.Message, StringComparison.Ordinal);

        // And a save that changes nothing else still passes — the product does not clash with itself.
        var noop = await ActivatorUtilities.CreateInstance<UpdateProduct>(scope.ServiceProvider)
            .HandleAsync(new UpdateProduct.Command(firstId, "shop", Repo, Compose, "main"), Ct);
        Assert.True(noop.IsSuccess, Describe(noop));
    }

    /// <summary>
    /// The release-retention floor is settable, clamped rather than refused, and left alone when the
    /// caller says nothing about it.
    /// </summary>
    /// <remarks>
    /// Clamped, not refused, because <c>ReleasePruner</c> clamps whatever it reads anyway (invariant 15):
    /// refusing here would be the only way for the stored number and the enforced number to disagree.
    /// The audit line says when a value was clamped, so an operator who typed 2 is not left believing it.
    /// </remarks>
    [Fact]
    public async Task Update_SetsTheReleaseRetentionFloor_ClampedAndAudited() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var productId = await AddProductAsync(scope.ServiceProvider, "shop", Repo, "main");
        var update = ActivatorUtilities.CreateInstance<UpdateProduct>(scope.ServiceProvider);

        var set = await update.HandleAsync(
            new UpdateProduct.Command(productId, "shop", Repo, Compose, "main", RetainReleases: 120), Ct);
        Assert.True(set.IsSuccess, Describe(set));
        Assert.Equal(120, set.Value.Product.RetainReleases);

        // Below the floor and above the ceiling both land on the bound the pruner would apply.
        var low = await update.HandleAsync(
            new UpdateProduct.Command(productId, "shop", Repo, Compose, "main", RetainReleases: 1), Ct);
        Assert.Equal(ReleasePruner.MinRetainReleases, low.Value.Product.RetainReleases);
        var high = await update.HandleAsync(
            new UpdateProduct.Command(productId, "shop", Repo, Compose, "main", RetainReleases: 100_000), Ct);
        Assert.Equal(ReleasePruner.MaxRetainReleases, high.Value.Product.RetainReleases);

        // Omitted means "leave it", not "reset it" — every pre-stage-7 caller posts the form without it.
        var untouched = await update.HandleAsync(
            new UpdateProduct.Command(productId, "shop", Repo, Compose, "main"), Ct);
        Assert.Equal(ReleasePruner.MaxRetainReleases, untouched.Value.Product.RetainReleases);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var details = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == "product.update")
            .OrderBy(e => e.Id)
            .Select(e => e.Detail!)
            .ToListAsync(Ct);
        Assert.Contains(details, d => d.Contains("release retention 50 → 120", StringComparison.Ordinal));
        Assert.Contains(details, d => d.Contains("(clamped from 1)", StringComparison.Ordinal));
    }

    /// <summary>
    /// "Who changed the credential behind this product?" is asked after a clone starts failing, so it
    /// gets its own action rather than a line inside a general update detail.
    /// </summary>
    [Fact]
    public async Task Update_RecordsACredentialChangeUnderItsOwnAuditAction() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var credential = new Credential { Name = "clone", Username = "git", Token = "t", CreatedAt = DateTimeOffset.UtcNow };
        db.Credentials.Add(credential);
        await db.SaveChangesAsync(Ct);
        var productId = await AddProductAsync(scope.ServiceProvider, "shop", Repo, "main");

        var update = ActivatorUtilities.CreateInstance<UpdateProduct>(scope.ServiceProvider);
        Assert.True((await update.HandleAsync(
            new UpdateProduct.Command(productId, "shop", Repo, Compose, "main", CredentialId: credential.Id),
            Ct)).IsSuccess);

        var row = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == "product.credential.change", Ct);
        Assert.Equal("products", row.Category);
        Assert.Equal("shop", row.Target);
        Assert.Contains("none → clone", row.Detail, StringComparison.Ordinal);
        // Nothing else changed, so there is no general update row to confuse it with.
        Assert.False(await db.AuditEvents.AsNoTracking().AnyAsync(e => e.Action == "product.update", Ct));
    }

    /// <summary>
    /// Moving a product's remote invalidates its CI link, because "which GitHub repo is this?" just got
    /// a new answer (ADR-0026 decision 7). It is cleared rather than re-resolved here so there stays one
    /// resolution path — the next <c>ci.getProductCi</c> parses the new URL and records what it finds.
    /// </summary>
    [Fact]
    public async Task Update_ClearsTheCiRepoLinkWhenTheRepositoryUrlChanges() {
        using var host = AuthTestHost.Start();
        int productId, ciRepoId;
        await using (var setup = host.Services.CreateAsyncScope()) {
            var db = setup.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var credential = new Credential {
                Name = "bot", Username = "git", Token = "t", CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Credentials.Add(credential);
            await db.SaveChangesAsync(Ct);
            var ciRepo = new CiRepo {
                Owner = "acme", Name = "shop", CredentialId = credential.Id, Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CiRepos.Add(ciRepo);
            await db.SaveChangesAsync(Ct);
            ciRepoId = ciRepo.Id;
            productId = await AddProductAsync(setup.ServiceProvider, "shop", Repo, "main");
            // The state ci.enableForProduct leaves behind, written directly so this test does not
            // depend on the CI module's handlers.
            await db.Products.Where(p => p.Id == productId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CiRepoId, ciRepoId), Ct);
        }

        await using var scope = host.Services.CreateAsyncScope();
        var update = ActivatorUtilities.CreateInstance<UpdateProduct>(scope.ServiceProvider);

        // An edit that leaves the URL alone leaves the link alone.
        Assert.True((await update.HandleAsync(
            new UpdateProduct.Command(productId, "shop", Repo, Compose, "develop"), Ct)).IsSuccess);
        Assert.Equal(ciRepoId, await CiRepoIdAsync(host, productId));

        Assert.True((await update.HandleAsync(
            new UpdateProduct.Command(productId, "shop", "https://github.com/acme/shop-next.git", Compose, "develop"),
            Ct)).IsSuccess);
        Assert.Null(await CiRepoIdAsync(host, productId));

        await using var check = host.Services.CreateAsyncScope();
        var readDb = check.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        // The CI repo itself survives — other products may still deploy it, and its runners keep running.
        Assert.True(await readDb.CiRepos.AsNoTracking().AnyAsync(r => r.Id == ciRepoId, Ct));
        var row = await readDb.AuditEvents.AsNoTracking()
            .Where(e => e.Action == "product.update").OrderByDescending(e => e.Id).FirstAsync(Ct);
        Assert.Contains("CI repository link cleared", row.Detail!, StringComparison.Ordinal);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────

    private static async Task<int?> CiRepoIdAsync(AuthTestHost host, int productId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Products.AsNoTracking()
            .Where(p => p.Id == productId).Select(p => p.CiRepoId).FirstAsync(Ct);
    }

    private static async Task<Result<CreateStack.Response>> CreateStackAsync(
        IServiceProvider services, string name, string repositoryUrl, string composeFilePath, string branch,
        int? productId = null, int? credentialId = null) {
        var handler = ActivatorUtilities.CreateInstance<CreateStack>(services);
        return await handler.HandleAsync(
            new CreateStack.Command(
                name, repositoryUrl, composeFilePath, branch, ComposeProjectName: name, CredentialId: credentialId,
                WebhookToken: null, WebhookEnabled: false, AutoDeployMode: null, AutoDeployTime: null,
                EnvVars: null, ProductId: productId),
            Ct);
    }

    private static async Task<Result<UpdateStack.Response>> UpdateStackAsync(
        IServiceProvider services, int id, string name, string repositoryUrl, string composeFilePath,
        string branch, int? credentialId, bool webhookEnabled = false) {
        var handler = ActivatorUtilities.CreateInstance<UpdateStack>(services);
        return await handler.HandleAsync(
            new UpdateStack.Command(
                id, name, repositoryUrl, composeFilePath, branch, ComposeProjectName: name,
                CredentialId: credentialId, WebhookToken: null, WebhookEnabled: webhookEnabled,
                AutoDeployMode: null, AutoDeployTime: null, EnvVars: null),
            Ct);
    }

    private static async Task<int> AddProductAsync(
        IServiceProvider services, string name, string repositoryUrl, string defaultBranch) {
        var handler = ActivatorUtilities.CreateInstance<CreateProduct>(services);
        var result = await handler.HandleAsync(
            new CreateProduct.Command(name, repositoryUrl, Compose, defaultBranch), Ct);
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value.Product.Id;
    }

    /// <summary>
    /// A tenant row exactly as <see cref="TenantProvisioningService"/> writes one — product by
    /// reference, no branch override — but without the initial deploy. That deploy is enqueued on a
    /// background worker whose status writes bump the stack's xmin at an arbitrary moment, which any
    /// test that then saves the stack twice would lose a concurrency check to. Provisioning's own
    /// behaviour is covered by the tests that only read afterwards.
    /// </summary>
    private static async Task<int> AddTenantAsync(AuthTestHost host, int templateId, string slug) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var template = await db.StackTemplates.AsNoTracking().SingleAsync(x => x.Id == templateId, Ct);
        var stack = new Stack {
            Name = $"{template.Name}-{slug}",
            ComposeProjectName = $"{template.Name}-{slug}",
            ProductId = template.ProductId,
            TemplateId = templateId,
            TenantSlug = slug,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);
        return stack.Id;
    }

    /// <summary>
    /// A template on the shared product, optionally carrying a <em>real</em> branch override.
    /// </summary>
    /// <remarks>
    /// The creation always asks for <see cref="DefaultBranch"/> and the override is written afterwards,
    /// which is the whole point: passing <paramref name="branch"/> to <c>templates.create</c> instead
    /// would find-or-create the product with that branch as its <em>default</em> and leave
    /// <c>BranchOverride</c> null. Product default and inherited branch would then be the same string,
    /// and a tenant test could no longer tell "compares against the inherited branch" from "compares
    /// against the product default" — the exact distinction the branch-inheritance tests exist to pin.
    /// </remarks>
    private static async Task<int> CreateTemplateAsync(AuthTestHost host, string name, string? branch = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<CreateTemplate>(scope.ServiceProvider);
        var result = await handler.HandleAsync(
            new CreateTemplate.Command(
                name, Repo, Compose, DefaultBranch, CredentialId: null,
                DomainPattern: "{tenant}.example.com", TargetServiceName: "web", TargetPort: 8080,
                BaseEnvVars: null),
            Ct);
        Assert.True(result.IsSuccess, Describe(result));
        var templateId = result.Value.Template.Id;

        if (branch is not null && branch != DefaultBranch) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.StackTemplates.Where(t => t.Id == templateId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.BranchOverride, branch), Ct);
        }
        return templateId;
    }

    private static string? Describe<T>(Result<T> result) => result.IsSuccess ? null : result.Error.Message;
}
