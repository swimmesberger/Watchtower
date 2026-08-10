using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
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
                RepositoryUrl = $"https://example.invalid/{name}.git",
                ComposeFilePath = "docker-compose.yml",
                Branch = "main",
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
    public static async Task<(int StackId, string Token)> AddCallerStackAsync(
        this WatchtowerApiFactory factory, string name, bool appApiEnabled = true) {
        var stackId = 0;
        var token = string.Empty;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var stack = NewStack(name);
            stack.AppApiToken = AppApiTokens.Generate();
            stack.AppApiEnabled = appApiEnabled;
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            stackId = stack.Id;
            token = stack.AppApiToken;
        });
        return (stackId, token);
    }

    /// <summary>Adds a tenant stack of <paramref name="templateId"/> with its primary route.</summary>
    public static async Task<int> AddTenantAsync(
        this WatchtowerApiFactory factory, int templateId, string slug,
        string? stackNamePrefix = null, string? domain = null, DeployStatus? lastDeployStatus = null) {
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

            db.Routes.Add(new Route {
                StackId = stack.Id,
                Domain = domain ?? $"{slug}.example.com",
                ServiceName = "web",
                ContainerPort = 8080,
                IsPrimary = true,
                Kind = DomainKind.Managed,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.StackEnvVars.Add(new StackEnvVar { StackId = stack.Id, Key = "SECRET", Value = "hunter2" });
            await db.SaveChangesAsync(ct);
            stackId = stack.Id;
        });
        return stackId;
    }

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
        RepositoryUrl = $"https://example.invalid/{name}.git",
        ComposeFilePath = "docker-compose.yml",
        Branch = "main",
        ComposeProjectName = name,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
