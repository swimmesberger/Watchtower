using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Tests;
using Xunit;

// Watchtower's own entity, not Microsoft.AspNetCore.Routing.Route — which ImplicitUsings pulls in here.
using Route = Watchtower.Application.Entities.Route;

namespace Watchtower.Api.Tests;

/// <summary>
/// Seeding helpers for the management API tests: the templates, the caller stack that holds the App API
/// token, the grants that authorize it, and the tenants it manages.
/// </summary>
/// <remarks>
/// Tenants are seeded as rows rather than created through the API because most tests are about what a
/// caller may <em>do with</em> an existing tenant; the creation endpoint has its own tests that go
/// through the real provisioning path.
/// </remarks>
internal static class MgmtTestEstate {
    /// <summary>Adds a template and returns its id.</summary>
    public static async Task<int> AddTemplateAsync(
        this WatchtowerApiFactory factory, string name, string domainPattern = "{tenant}.example.com") {
        var templateId = 0;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
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
            templateId = template.Id;
        });
        return templateId;
    }

    /// <summary>Adds a stack with an App API token and returns its id and that token.</summary>
    /// <remarks>
    /// <paramref name="domain"/> gives the caller a primary route of its own. The tenant-discovery
    /// endpoints need one: a stack's route domains are the audiences it may present an assertion for.
    /// </remarks>
    public static async Task<(int StackId, string Token)> AddCallerStackAsync(
        this WatchtowerApiFactory factory, string name, bool appApiEnabled = true, string? domain = null) {
        var stackId = 0;
        var token = string.Empty;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var ct = TestContext.Current.CancellationToken;
            var stack = NewStack(name);
            stack.AppApiToken = AppApiTokens.Generate();
            stack.AppApiEnabled = appApiEnabled;
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(ct);
            stackId = stack.Id;
            token = stack.AppApiToken;

            if (domain is null) return;
            db.Routes.Add(new Route {
                StackId = stack.Id,
                Domain = domain,
                ServiceName = "web",
                ContainerPort = 8080,
                IsPrimary = true,
                Kind = DomainKind.Managed,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        });
        return (stackId, token);
    }

    /// <summary>Adds a tenant stack of <paramref name="templateId"/> with its primary route.</summary>
    /// <remarks>
    /// <paramref name="accessMode"/> and <paramref name="withRoute"/> exist for the tenant-switcher tests:
    /// the route's mode is what decides whether a visitor sees the tenant at all, and a tenant with no
    /// route is the "nothing to switch to" case.
    /// </remarks>
    public static async Task<int> AddTenantAsync(
        this WatchtowerApiFactory factory, int templateId, string slug,
        string? stackNamePrefix = null, string? domain = null, DeployStatus? lastDeployStatus = null,
        AccessMode accessMode = AccessMode.Public, bool withRoute = true) {
        var stackId = 0;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var ct = TestContext.Current.CancellationToken;
            var name = $"{stackNamePrefix ?? "billing"}-{slug}";
            var stack = NewStack(name);
            stack.TemplateId = templateId;
            stack.TenantSlug = slug;
            stack.AppApiToken = AppApiTokens.Generate();
            stack.LastDeployStatus = lastDeployStatus;
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(ct);

            if (withRoute)
                db.Routes.Add(new Route {
                    StackId = stack.Id,
                    Domain = domain ?? $"{slug}.example.com",
                    ServiceName = "web",
                    ContainerPort = 8080,
                    IsPrimary = true,
                    Kind = DomainKind.Managed,
                    AccessMode = accessMode,
                    CreatedAt = DateTimeOffset.UtcNow,
                });
            db.StackEnvVars.Add(new StackEnvVar { StackId = stack.Id, Key = "SECRET", Value = "hunter2" });
            await db.SaveChangesAsync(ct);
            stackId = stack.Id;
        });
        return stackId;
    }

    /// <summary>The App API token a seeded stack authenticates with.</summary>
    public static Task<string> AppApiTokenAsync(this WatchtowerApiFactory factory, int stackId) =>
        factory.ReadAsync(db => db.Stacks.AsNoTracking()
            .Where(s => s.Id == stackId)
            .Select(s => s.AppApiToken!)
            .SingleAsync(TestContext.Current.CancellationToken));

    /// <summary>The id of a stack's primary route — what a <see cref="RouteAccessGrant"/> points at.</summary>
    public static Task<int> PrimaryRouteIdAsync(this WatchtowerApiFactory factory, int stackId) =>
        factory.ReadAsync(db => db.Routes.AsNoTracking()
            .Where(r => r.StackId == stackId && r.IsPrimary)
            .Select(r => r.Id)
            .SingleAsync(TestContext.Current.CancellationToken));

    /// <summary>
    /// Writes a raw <c>access_mode</c> value straight into the routes table, bypassing the enum.
    /// </summary>
    /// <remarks>
    /// The only way to model what a downgrade leaves behind: a newer build persists a mode this one has no
    /// name for. A numeric value is what EF itself would have written, and it reads back as an undefined
    /// enum value rather than throwing — which is precisely the input the policy has to fail closed on.
    /// </remarks>
    public static Task SetRawAccessModeAsync(this WatchtowerApiFactory factory, int routeId, string raw) =>
        factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            await db.Database.ExecuteSqlAsync(
                $"UPDATE routes SET access_mode = {raw} WHERE id = {routeId}",
                TestContext.Current.CancellationToken);
        });

    /// <summary>Lets <paramref name="stackId"/> manage <paramref name="templateId"/>'s tenants.</summary>
    public static Task GrantManagementAsync(
        this WatchtowerApiFactory factory, int stackId, int templateId, bool allowDelete = false) =>
        factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            db.TemplateManagementGrants.Add(new TemplateManagementGrant {
                StackId = stackId, TemplateId = templateId, AllowDelete = allowDelete,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    /// <summary>Adds a deploy event in a given state — the in-flight ones are what teardown refuses under.</summary>
    public static Task AddDeployEventAsync(
        this WatchtowerApiFactory factory, int stackId, string status, string? output = null) =>
        factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            db.DeployEvents.Add(new DeployEvent {
                StackId = stackId,
                TriggeredBy = "test",
                Status = status,
                Output = output,
                StartedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    /// <summary>Reads a stack's environment variables, to check what a creation actually stored.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> EnvOfAsync(
        this WatchtowerApiFactory factory, int stackId) {
        IReadOnlyDictionary<string, string> env = new Dictionary<string, string>();
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            env = await db.StackEnvVars.AsNoTracking()
                .Where(v => v.StackId == stackId)
                .ToDictionaryAsync(v => v.Key, v => v.Value, TestContext.Current.CancellationToken);
        });
        return env;
    }

    /// <summary>Runs a read against the host's database.</summary>
    public static async Task<T> ReadAsync<T>(
        this WatchtowerApiFactory factory, Func<WatchtowerDbContext, Task<T>> read) {
        var value = default(T)!;
        await factory.WithScopeAsync(async sp =>
            value = await read(sp.GetRequiredService<WatchtowerDbContext>()));
        return value;
    }

    private static Stack NewStack(string name) => new() {
        Name = name,
        ComposeProjectName = name,
        Product = TestProducts.New(name),
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
