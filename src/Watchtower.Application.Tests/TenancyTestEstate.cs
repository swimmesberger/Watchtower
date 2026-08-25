using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Seeding helpers for the tenancy tests: templates, stacks, tenants and management grants. Rows are
/// written directly because these are the <em>preconditions</em> of the code under test — the code that
/// creates tenants for real is what the provisioning tests are asserting about.
/// </summary>
internal static class TenancyTestEstate {
    /// <summary>Adds a template and returns its id.</summary>
    public static async Task<int> AddTemplateAsync(
        this AuthTestHost host, string name, string domainPattern = "{tenant}.example.com",
        params (string Key, string Value)[] baseEnv) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var template = new StackTemplate {
            Name = name,
            Product = TestProducts.New(name),
            DomainPattern = domainPattern,
            TargetServiceName = "web",
            TargetPort = 8080,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.StackTemplates.Add(template);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        foreach (var (key, value) in baseEnv)
            db.StackTemplateEnvVars.Add(new StackTemplateEnvVar {
                TemplateId = template.Id, Key = key, Value = value,
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return template.Id;
    }

    /// <summary>Adds a standalone stack (or, with <paramref name="templateId"/>, a tenant) and returns its id.</summary>
    public static async Task<int> AddStackAsync(
        this AuthTestHost host, string name, int? templateId = null, string? tenantSlug = null,
        string? composeProjectName = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var stack = new Stack {
            Name = name,
            ComposeProjectName = composeProjectName ?? name,
            ProductId = await ProductIdForAsync(db, templateId, name, ct),
            TemplateId = templateId,
            TenantSlug = tenantSlug,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return stack.Id;
    }

    /// <summary>Adds a route for a stack, which is what makes its domain taken.</summary>
    public static async Task AddRouteAsync(this AuthTestHost host, int stackId, string domain) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.Routes.Add(new Entities.Route {
            StackId = stackId,
            Domain = domain,
            ServiceName = "web",
            ContainerPort = 8080,
            IsPrimary = true,
            Kind = DomainKind.Managed,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Adds a deploy event in a given state — the in-flight ones are what teardown refuses under.</summary>
    public static async Task AddDeployEventAsync(this AuthTestHost host, int stackId, string status) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.DeployEvents.Add(new DeployEvent {
            StackId = stackId, TriggeredBy = "test", Status = status, StartedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Lets <paramref name="stackId"/> manage <paramref name="templateId"/>'s tenants.</summary>
    public static async Task AddGrantAsync(
        this AuthTestHost host, int stackId, int templateId, bool allowDelete = false) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.TemplateManagementGrants.Add(new TemplateManagementGrant {
            StackId = stackId, TemplateId = templateId, AllowDelete = allowDelete,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The product a tenant stack must reference: its template's, since ADR-0026 links rather than
    /// copies. A standalone stack gets one of its own, named after it.
    /// </summary>
    private static async Task<int> ProductIdForAsync(
        WatchtowerDbContext db, int? templateId, string name, CancellationToken ct) {
        if (templateId is { } id) {
            return await db.StackTemplates.Where(t => t.Id == id).Select(t => t.ProductId).FirstAsync(ct);
        }
        var product = TestProducts.New(name);
        db.Products.Add(product);
        await db.SaveChangesAsync(ct);
        return product.Id;
    }
}
