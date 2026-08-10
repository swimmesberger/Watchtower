using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Xunit;

// Watchtower's own entity, not Microsoft.AspNetCore.Routing.Route.
using Route = Watchtower.Application.Entities.Route;

namespace Watchtower.Application.Tests;

/// <summary>
/// Seeding helpers for the access-control tests: the accounts, groups, routes and grants a protected
/// estate consists of. The counterpart of <c>Watchtower.Api.Tests.AccessTestEstate</c>, which does the
/// same job against a running host.
/// </summary>
/// <remarks>
/// Accounts go through <c>UserManager</c> so a test never sets up a state the application itself could not
/// have produced. Groups, routes and grants are written directly, because those are the
/// <em>preconditions</em> of the code under test rather than its subject — the handlers that create them
/// for real are what the module tests assert about (the same split <see cref="TenancyTestEstate"/> makes).
/// </remarks>
internal static class AccessTestEstate {
    private const string Password = "correct-horse-battery";

    /// <summary>Creates an account through <c>UserManager</c> and returns its id.</summary>
    public static async Task<int> AddUserAsync(this AuthTestHost host, string userName, bool isAdmin = false) {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = AuthTestHost.NewUser(userName);
        user.IsAdmin = isAdmin;
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user.Id;
    }

    /// <summary>Adds a stack and a route on it, and returns the route.</summary>
    public static async Task<Route> AddRouteAsync(
        this AuthTestHost host, string domain, AccessMode mode = AccessMode.Public) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;

        var name = domain.Split('.')[0];
        var stack = new Stack {
            Name = name,
            RepositoryUrl = $"https://example.invalid/{name}.git",
            ComposeFilePath = "docker-compose.yml",
            Branch = "main",
            ComposeProjectName = name,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(ct);

        var route = new Route {
            StackId = stack.Id,
            Domain = domain,
            ServiceName = "web",
            ContainerPort = 8080,
            AccessMode = mode,
        };
        db.Routes.Add(route);
        await db.SaveChangesAsync(ct);
        return route;
    }

    /// <summary>Creates a group holding <paramref name="memberIds"/> and returns its id.</summary>
    public static async Task<int> AddGroupAsync(
        this AuthTestHost host, string name, params int[] memberIds) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;

        var group = new Group {
            Name = name,
            NormalizedName = name.ToUpperInvariant(),
            CreatedAt = host.Time.GetUtcNow(),
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);

        foreach (var userId in memberIds)
            db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = userId });
        await db.SaveChangesAsync(ct);
        return group.Id;
    }

    /// <summary>Lets one account through a <see cref="AccessMode.Restricted"/> route.</summary>
    public static Task GrantUserAsync(this AuthTestHost host, int routeId, int userId) =>
        AddGrantAsync(host, new RouteAccessGrant { RouteId = routeId, UserId = userId });

    /// <summary>Lets every member of one group through a <see cref="AccessMode.Restricted"/> route.</summary>
    public static Task GrantGroupAsync(this AuthTestHost host, int routeId, int groupId) =>
        AddGrantAsync(host, new RouteAccessGrant { RouteId = routeId, GroupId = groupId });

    private static async Task AddGrantAsync(AuthTestHost host, RouteAccessGrant grant) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.RouteAccessGrants.Add(grant);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Removes one account from one group, the way an administrator revoking membership does.</summary>
    public static async Task RemoveFromGroupAsync(this AuthTestHost host, int groupId, int userId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var member = await db.GroupMembers.SingleAsync(m => m.GroupId == groupId && m.UserId == userId, ct);
        db.GroupMembers.Remove(member);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The audit kinds written so far, oldest first.</summary>
    public static async Task<IReadOnlyList<string>> AuditKindsAsync(this AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuthEvents.OrderBy(e => e.Id)
            .Select(e => e.Kind)
            .ToListAsync(TestContext.Current.CancellationToken);
    }
}
