using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Modules.Tenancy;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="TenantProvisioningService"/> — the one code path behind both
/// <c>templates.addTenant</c> and <c>POST /api/mgmt/templates/{id}/tenants</c>.
/// </summary>
/// <remarks>
/// The collision rules are the point. Each of them protects something specific: a duplicate slug or stack
/// name would make the tenant unaddressable, a duplicate domain would route two stacks to one host name,
/// and a duplicate compose project name would let one tenant's App API token read another tenant's
/// containers and logs. They are reported as <c>Conflict</c> so the REST surface can answer 409 while the
/// RPC handler keeps projecting them as validation failures, which is what it has always returned.
/// </remarks>
public sealed class TenantProvisioningServiceTests {
    /// <summary>Accept deploys without running them; see <see cref="QueuedOnlyDeployQueueService"/>.</summary>
    private static readonly Action<IServiceCollection> WithQueuedOnlyDeploys = services => {
        services.RemoveAll<DeployQueueService>();
        services.AddSingleton<DeployQueueService>(
            sp => ActivatorUtilities.CreateInstance<QueuedOnlyDeployQueueService>(sp));
    };

    [Fact]
    public async Task Provision_CreatesTheStack_ItsEnvironment_ItsRoute_AndQueuesTheDeploy() {
        using var host = AuthTestHost.Start(WithQueuedOnlyDeploys);
        var templateId = await host.AddTemplateAsync(
            "billing", "{tenant}.example.com", ("SHARED", "base"), ("KEPT", "base"));

        var result = await ProvisionAsync(host, templateId, "acme", [new TemplateEnvVarInput("SHARED", "override")]);

        Assert.Equal(TenantProvisionStatus.Created, result.Status);
        Assert.Equal("acme", result.Tenant!.TenantSlug);
        Assert.Equal("billing-acme", result.Tenant.StackName);
        Assert.Equal("acme.example.com", result.Tenant.Domain);
        Assert.Equal("queued", result.DeployStatus);
        Assert.True(result.DeployEventId > 0);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;

        var stack = await db.Stacks.SingleAsync(s => s.Id == result.Tenant.StackId, ct);
        Assert.Equal(templateId, stack.TemplateId);
        Assert.Equal("acme", stack.TenantSlug);
        Assert.Equal("billing-acme", stack.ComposeProjectName);
        // Every tenant gets its own token, so no tenant can read another tenant's status or logs.
        Assert.StartsWith(AppApiTokens.Prefix, stack.AppApiToken);
        Assert.True(stack.AppApiEnabled);

        // Overrides win over the template's base values; untouched base values survive.
        var env = await db.StackEnvVars.Where(v => v.StackId == stack.Id)
            .ToDictionaryAsync(v => v.Key, v => v.Value, ct);
        Assert.Equal("override", env["SHARED"]);
        Assert.Equal("base", env["KEPT"]);

        var route = await db.Routes.SingleAsync(r => r.StackId == stack.Id, ct);
        Assert.Equal("acme.example.com", route.Domain);
        Assert.True(route.IsPrimary);

        // The deploy is enqueued only after the transaction commits, and it names the right stack.
        var queue = (QueuedOnlyDeployQueueService)host.Services.GetRequiredService<DeployQueueService>();
        Assert.Equal([(stack.Id, TenantProvisioningService.CreateTrigger)], queue.Calls);
        var deployEvent = await db.DeployEvents.SingleAsync(e => e.Id == result.DeployEventId, ct);
        Assert.Equal(stack.Id, deployEvent.StackId);
        Assert.Equal("queued", deployEvent.Status);
    }

    [Fact]
    public async Task Provision_NormalizesTheSlug() {
        using var host = AuthTestHost.Start(WithQueuedOnlyDeploys);
        var templateId = await host.AddTemplateAsync("billing");

        var result = await ProvisionAsync(host, templateId, "  ACME  ");

        Assert.Equal(TenantProvisionStatus.Created, result.Status);
        Assert.Equal("acme", result.Tenant!.TenantSlug);
        Assert.Equal("acme.example.com", result.Tenant.Domain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-leading-hyphen")]
    [InlineData("has spaces")]
    [InlineData("has.dots")]
    [InlineData("has_underscore")]
    public async Task Provision_RejectsAnUnusableSlug(string slug) {
        using var host = AuthTestHost.Start(WithQueuedOnlyDeploys);
        var templateId = await host.AddTemplateAsync("billing");

        var result = await ProvisionAsync(host, templateId, slug);

        Assert.Equal(TenantProvisionStatus.Validation, result.Status);
        await AssertNoTenantAsync(host);
    }

    [Fact]
    public async Task Provision_RejectsDuplicateEnvKeys() {
        using var host = AuthTestHost.Start(WithQueuedOnlyDeploys);
        var templateId = await host.AddTemplateAsync("billing");

        var result = await ProvisionAsync(host, templateId, "acme", [
            new TemplateEnvVarInput("KEY", "one"),
            new TemplateEnvVarInput("KEY", "two"),
        ]);

        Assert.Equal(TenantProvisionStatus.Validation, result.Status);
        Assert.Contains("KEY", result.Error);
        await AssertNoTenantAsync(host);
    }

    [Fact]
    public async Task Provision_ReportsAnUnknownTemplate() {
        using var host = AuthTestHost.Start(WithQueuedOnlyDeploys);

        var result = await ProvisionAsync(host, 404, "acme");

        Assert.Equal(TenantProvisionStatus.TemplateNotFound, result.Status);
    }

    [Fact]
    public async Task Provision_RejectsASlugTheTemplateAlreadyHas() {
        using var host = AuthTestHost.Start(WithQueuedOnlyDeploys);
        var templateId = await host.AddTemplateAsync("billing");
        Assert.Equal(TenantProvisionStatus.Created, (await ProvisionAsync(host, templateId, "acme")).Status);

        var result = await ProvisionAsync(host, templateId, "acme");

        Assert.Equal(TenantProvisionStatus.Conflict, result.Status);
    }

    [Fact]
    public async Task Provision_RejectsAStackNameAlreadyTaken() {
        using var host = AuthTestHost.Start(WithQueuedOnlyDeploys);
        var templateId = await host.AddTemplateAsync("billing");
        // The derived stack name is "{template}-{slug}", which this standalone stack already occupies.
        await host.AddStackAsync("billing-acme", composeProjectName: "unrelated-project");

        var result = await ProvisionAsync(host, templateId, "acme");

        Assert.Equal(TenantProvisionStatus.Conflict, result.Status);
        Assert.Contains("billing-acme", result.Error);
    }

    [Fact]
    public async Task Provision_RejectsADomainAlreadyRouted() {
        using var host = AuthTestHost.Start(WithQueuedOnlyDeploys);
        var templateId = await host.AddTemplateAsync("billing");
        var otherStack = await host.AddStackAsync("unrelated");
        await host.AddRouteAsync(otherStack, "acme.example.com");

        var result = await ProvisionAsync(host, templateId, "acme");

        Assert.Equal(TenantProvisionStatus.Conflict, result.Status);
        Assert.Contains("acme.example.com", result.Error);
    }

    /// <summary>
    /// The compose project name is what container lookups resolve by, so a collision is the one that
    /// would cross the tenant isolation boundary — and it is compared case-insensitively, exactly as
    /// Compose treats project names.
    /// </summary>
    [Fact]
    public async Task Provision_RejectsAComposeProjectNameAlreadyUsed() {
        using var host = AuthTestHost.Start(WithQueuedOnlyDeploys);
        var templateId = await host.AddTemplateAsync("billing");
        await host.AddStackAsync("squatter", composeProjectName: "BILLING-ACME");

        var result = await ProvisionAsync(host, templateId, "acme");

        Assert.Equal(TenantProvisionStatus.Conflict, result.Status);
        Assert.Contains("project name", result.Error);
    }

    /// <summary>A refused creation must leave nothing behind — no stack, no route, no queued deploy.</summary>
    [Fact]
    public async Task Provision_LeavesNothingBehindWhenItRefuses() {
        using var host = AuthTestHost.Start(WithQueuedOnlyDeploys);
        var templateId = await host.AddTemplateAsync("billing");
        var otherStack = await host.AddStackAsync("unrelated");
        await host.AddRouteAsync(otherStack, "acme.example.com");

        Assert.Equal(TenantProvisionStatus.Conflict, (await ProvisionAsync(host, templateId, "acme")).Status);

        await AssertNoTenantAsync(host);
        var queue = (QueuedOnlyDeployQueueService)host.Services.GetRequiredService<DeployQueueService>();
        Assert.Empty(queue.Calls);
    }

    // -- Helpers ---------------------------------------------------------------------------------

    private static async Task<TenantProvisionResult> ProvisionAsync(
        AuthTestHost host, int templateId, string? slug, IReadOnlyList<TemplateEnvVarInput>? env = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var provisioning = scope.ServiceProvider.GetRequiredService<TenantProvisioningService>();
        return await provisioning.ProvisionAsync(templateId, slug, env, TestContext.Current.CancellationToken);
    }

    private static async Task AssertNoTenantAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.Stacks.AnyAsync(s => s.TemplateId != null, TestContext.Current.CancellationToken));
    }
}
