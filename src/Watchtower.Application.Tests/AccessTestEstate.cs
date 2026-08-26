using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
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

    /// <summary>
    /// Adds a realm and returns its id. <paramref name="loginDomain"/> also creates the realm's login
    /// route — the <see cref="RouteTarget.Watchtower"/> row its login page is served on (ADR-0023) —
    /// while null leaves the realm with no login host, the state of one whose DNS is not ready yet.
    /// </summary>
    public static async Task<int> AddRealmAsync(
        this AuthTestHost host, string slug, string? loginDomain = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;
        var realm = new Realm {
            Name = slug,
            Slug = slug,
            CreatedAt = host.Time.GetUtcNow(),
        };
        db.Realms.Add(realm);
        await db.SaveChangesAsync(ct);

        if (loginDomain is not null) {
            var route = NewWatchtowerRoute(host, loginDomain, realm.Id);
            db.Routes.Add(route);
            await db.SaveChangesAsync(ct);
            realm.LoginRouteId = route.Id;
            await db.SaveChangesAsync(ct);
        }
        return realm.Id;
    }

    /// <summary>
    /// Adds a <see cref="RouteTarget.Watchtower"/> route for <paramref name="realmId"/> and returns it,
    /// optionally designating it as that realm's login route.
    /// </summary>
    public static async Task<Route> AddWatchtowerRouteAsync(
        this AuthTestHost host,
        string domain,
        int realmId = Realm.SystemRealmId,
        bool makeLoginRoute = false) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;

        var route = NewWatchtowerRoute(host, domain, realmId);
        db.Routes.Add(route);
        await db.SaveChangesAsync(ct);

        if (makeLoginRoute) {
            var realm = await db.Realms.SingleAsync(r => r.Id == realmId, ct);
            realm.LoginRouteId = route.Id;
            await db.SaveChangesAsync(ct);
        }
        return route;
    }

    /// <summary>The shape the check constraint accepts for a Watchtower route: no stack, a realm, Public.</summary>
    private static Route NewWatchtowerRoute(AuthTestHost host, string domain, int realmId) => new() {
        Target = RouteTarget.Watchtower,
        StackId = null,
        RealmId = realmId,
        Domain = domain,
        ServiceName = string.Empty,
        ContainerPort = 0,
        TlsEnabled = true,
        AccessMode = AccessMode.Public,
        Status = RouteStatus.Pending,
        CreatedAt = host.Time.GetUtcNow(),
    };

    /// <summary>
    /// Creates an account through <c>UserManager</c> and returns its id. The realm context is pinned first,
    /// exactly as the login endpoint and the administrative handlers do it — otherwise Identity's duplicate
    /// check would be answered about the wrong population.
    /// </summary>
    public static async Task<int> AddUserAsync(
        this AuthTestHost host, string userName, bool isAdmin = false, int realmId = Realm.SystemRealmId) {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<IRealmContext>().SetRealm(realmId);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = AuthTestHost.NewUser(userName);
        user.IsAdmin = isAdmin;
        user.RealmId = realmId;
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user.Id;
    }

    /// <summary>
    /// Adds a template in <paramref name="realmId"/> and returns its id — the realm-aware counterpart of
    /// <see cref="TenancyTestEstate.AddTemplateAsync"/>, which seeds system-realm categories for the
    /// provisioning tests.
    /// </summary>
    public static async Task<int> AddRealmTemplateAsync(
        this AuthTestHost host, string name, int realmId = Realm.SystemRealmId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var template = new StackTemplate {
            RealmId = realmId,
            Name = name,
            Product = TestProducts.New(name),
            DomainPattern = $"{{tenant}}.{name}.example.invalid",
            TargetServiceName = "web",
            TargetPort = 8080,
            CreatedAt = host.Time.GetUtcNow(),
        };
        db.StackTemplates.Add(template);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return template.Id;
    }

    /// <summary>
    /// Adds a stack and a route on it, and returns the route. Passing <paramref name="templateId"/> makes
    /// the stack a tenant of that category, which is how a route ends up in a non-system realm.
    /// </summary>
    public static async Task<Route> AddRouteAsync(
        this AuthTestHost host,
        string domain,
        AccessMode mode = AccessMode.Public,
        int? templateId = null,
        string? bypassPaths = null,
        IdentityHeaderMode identityHeaderMode = IdentityHeaderMode.None) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;

        var name = domain.Split('.')[0];
        var stack = new Stack {
            Name = name,
            ComposeProjectName = name,
            ProductId = await ProductIdForAsync(db, templateId, name, ct),
            TemplateId = templateId,
            TenantSlug = templateId is null ? null : name,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(ct);

        var route = new Route {
            Target = RouteTarget.Service,
            StackId = stack.Id,
            Domain = domain,
            ServiceName = "web",
            ContainerPort = 8080,
            AccessMode = mode,
            BypassPaths = bypassPaths,
            IdentityHeaderMode = identityHeaderMode,
        };
        db.Routes.Add(route);
        await db.SaveChangesAsync(ct);
        return route;
    }

    /// <summary>Creates a group in the system realm holding <paramref name="memberIds"/> and returns its id.</summary>
    public static Task<int> AddGroupAsync(this AuthTestHost host, string name, params int[] memberIds) =>
        AddGroupInRealmAsync(host, name, Realm.SystemRealmId, memberIds);

    /// <summary>Creates a group in <paramref name="realmId"/> holding <paramref name="memberIds"/>.</summary>
    public static async Task<int> AddGroupInRealmAsync(
        this AuthTestHost host, string name, int realmId, params int[] memberIds) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;

        var group = new Group {
            RealmId = realmId,
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
        return await db.AuditEvents.OrderBy(e => e.Id)
            .Select(e => e.Action)
            .ToListAsync(TestContext.Current.CancellationToken);
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
