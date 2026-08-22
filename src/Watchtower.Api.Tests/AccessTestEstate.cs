using Microsoft.AspNetCore.Identity;
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
/// Seeding helpers for the forward-auth tests: the routes, accounts, grants and sessions a protected
/// estate consists of. Written against the running host's own services (<c>UserManager</c>,
/// <see cref="AuthSessionService"/>) rather than inserting rows directly, so a test never sets up a state
/// the application itself could not have produced.
/// </summary>
internal static class AccessTestEstate {
    private const string Password = "correct-horse-battery";

    /// <summary>
    /// Adds a realm and returns its id. <paramref name="loginDomain"/> also creates and designates the
    /// realm's login route (ADR-0021); null models "DNS not ready", where the realm has no login host.
    /// </summary>
    public static async Task<int> AddRealmAsync(
        this WatchtowerApiFactory factory, string slug, string? loginDomain = null) {
        var realmId = 0;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var ct = TestContext.Current.CancellationToken;
            var realm = new Realm { Name = slug, Slug = slug, CreatedAt = DateTimeOffset.UtcNow };
            db.Realms.Add(realm);
            await db.SaveChangesAsync(ct);

            if (loginDomain is not null) {
                var route = NewWatchtowerRoute(loginDomain, realm.Id);
                db.Routes.Add(route);
                await db.SaveChangesAsync(ct);
                realm.LoginRouteId = route.Id;
                await db.SaveChangesAsync(ct);
            }
            realmId = realm.Id;
        });
        return realmId;
    }

    /// <summary>
    /// Adds a <see cref="RouteTarget.Watchtower"/> route and returns its id, optionally designating it as
    /// <paramref name="realmId"/>'s login route.
    /// </summary>
    public static async Task<int> AddWatchtowerRouteAsync(
        this WatchtowerApiFactory factory,
        string domain,
        int realmId = Realm.SystemRealmId,
        bool makeLoginRoute = false) {
        var routeId = 0;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var ct = TestContext.Current.CancellationToken;
            var route = NewWatchtowerRoute(domain, realmId);
            db.Routes.Add(route);
            await db.SaveChangesAsync(ct);
            if (makeLoginRoute) {
                var realm = await db.Realms.SingleAsync(r => r.Id == realmId, ct);
                realm.LoginRouteId = route.Id;
                await db.SaveChangesAsync(ct);
            }
            routeId = route.Id;
        });
        return routeId;
    }

    /// <summary>The shape the check constraint accepts for a Watchtower route: no stack, a realm, Public.</summary>
    private static Route NewWatchtowerRoute(string domain, int realmId) => new() {
        Target = RouteTarget.Watchtower,
        StackId = null,
        RealmId = realmId,
        Domain = domain,
        ServiceName = string.Empty,
        ContainerPort = 0,
        TlsEnabled = true,
        AccessMode = AccessMode.Public,
        Status = RouteStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Adds a category in <paramref name="realmId"/> — how a route ends up in a non-system realm.</summary>
    public static async Task<int> AddTemplateAsync(
        this WatchtowerApiFactory factory, string name, int realmId) {
        var templateId = 0;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var template = new StackTemplate {
                RealmId = realmId,
                Name = name,
                RepositoryUrl = $"https://example.invalid/{name}.git",
                ComposeFilePath = "docker-compose.yml",
                Branch = "main",
                DomainPattern = $"{{tenant}}.{name}.example.invalid",
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

    /// <summary>
    /// Adds a stack and a route for <paramref name="domain"/> and returns the route id. With
    /// <paramref name="templateId"/> the stack is a tenant of that category, so the route inherits its realm.
    /// </summary>
    /// <param name="stackName">
    /// Overrides the stack name, which otherwise follows the domain's first label. Only worth setting where
    /// the difference between the two is the thing under test.
    /// </param>
    /// <param name="tlsEnabled">Whether the proxy terminates HTTPS for the domain, as the real column does.</param>
    public static async Task<int> AddRouteAsync(
        this WatchtowerApiFactory factory, string domain, AccessMode mode, string? bypassPaths = null,
        IdentityHeaderMode identityHeaderMode = IdentityHeaderMode.None, int? templateId = null,
        string? stackName = null, bool tlsEnabled = true) {
        var routeId = 0;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var ct = TestContext.Current.CancellationToken;

            var label = domain.Split('.')[0];
            var name = stackName ?? label;
            var stack = new Stack {
                Name = name,
                RepositoryUrl = $"https://example.invalid/{name}.git",
                ComposeFilePath = "docker-compose.yml",
                Branch = "main",
                ComposeProjectName = name,
                TemplateId = templateId,
                TenantSlug = templateId is null ? null : label,
            };
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(ct);

            var route = new Route {
                Target = RouteTarget.Service,
                StackId = stack.Id,
                Domain = domain,
                ServiceName = "web",
                ContainerPort = 8080,
                IsPrimary = true,
                AccessMode = mode,
                IdentityHeaderMode = identityHeaderMode,
                BypassPaths = bypassPaths,
                TlsEnabled = tlsEnabled,
            };
            db.Routes.Add(route);
            await db.SaveChangesAsync(ct);
            routeId = route.Id;
        });
        return routeId;
    }

    /// <summary>
    /// Adds another domain for the stack <paramref name="routeId"/> belongs to, carrying the same access
    /// mode so that what distinguishes it is only its service and <see cref="Route.IsPrimary"/>.
    /// </summary>
    /// <param name="serviceName">
    /// Which service the new domain forwards to. Omitted it reuses the existing route's, making the new
    /// domain a true <em>alias</em> — a second name for one entry point. Supplying a different one models a
    /// stack that serves two (a UI and its API), which is two entry points and not an alias at all.
    /// </param>
    public static async Task<int> AddStackRouteAsync(
        this WatchtowerApiFactory factory, int routeId, string domain, string? serviceName = null) {
        var addedId = 0;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var ct = TestContext.Current.CancellationToken;

            var existing = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, ct);
            var added = new Route {
                Target = RouteTarget.Service,
                StackId = existing.StackId,
                Domain = domain,
                ServiceName = serviceName ?? existing.ServiceName,
                ContainerPort = existing.ContainerPort,
                IsPrimary = false,
                AccessMode = existing.AccessMode,
                TlsEnabled = existing.TlsEnabled,
            };
            db.Routes.Add(added);
            await db.SaveChangesAsync(ct);
            addedId = added.Id;
        });
        return addedId;
    }

    /// <summary>When the session behind <paramref name="rawToken"/> currently expires.</summary>
    public static async Task<DateTimeOffset> SessionExpiryAsync(
        this WatchtowerApiFactory factory, string rawToken) {
        var expiry = default(DateTimeOffset);
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var hash = AuthSessionService.HashToken(rawToken);
            expiry = (await db.AuthSessions.AsNoTracking()
                .SingleAsync(s => s.TokenHash == hash, TestContext.Current.CancellationToken)).ExpiresAt;
        });
        return expiry;
    }

    /// <summary>
    /// Moves a session's expiry, the way an hour of idling would. Used to put it inside the renewal window
    /// (less than half the sliding lifetime left), which is the only state in which "does this endpoint
    /// slide the session?" is a question with two possible answers.
    /// </summary>
    public static Task SetSessionExpiryAsync(
        this WatchtowerApiFactory factory, string rawToken, DateTimeOffset expiresAt) =>
        factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var ct = TestContext.Current.CancellationToken;
            var hash = AuthSessionService.HashToken(rawToken);
            var session = await db.AuthSessions.SingleAsync(s => s.TokenHash == hash, ct);
            session.ExpiresAt = expiresAt;
            await db.SaveChangesAsync(ct);
        });

    /// <summary>
    /// Creates an account through <c>UserManager</c> and returns its id. The realm context is pinned first,
    /// exactly as the login endpoint does it, so Identity's duplicate check is answered about the realm the
    /// account is going into.
    /// </summary>
    public static async Task<int> AddUserAsync(
        this WatchtowerApiFactory factory, string userName, string? email = null, bool disabled = false,
        int realmId = Realm.SystemRealmId, string? password = null) {
        var userId = 0;
        await factory.WithScopeAsync(async sp => {
            sp.GetRequiredService<IRealmContext>().SetRealm(realmId);
            var users = sp.GetRequiredService<UserManager<User>>();
            var user = new User {
                RealmId = realmId,
                UserName = userName,
                NormalizedUserName = userName.ToUpperInvariant(),
                Email = email,
                Disabled = disabled,
                PasswordHash = string.Empty,
                SecurityStamp = string.Empty,
                ConcurrencyStamp = string.Empty,
            };
            var created = await users.CreateAsync(user, password ?? Password);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
            userId = user.Id;
        });
        return userId;
    }

    /// <summary>Lets <paramref name="userId"/> through a <see cref="AccessMode.Restricted"/> route.</summary>
    public static Task GrantAsync(this WatchtowerApiFactory factory, int routeId, int userId) =>
        factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            db.RouteAccessGrants.Add(new RouteAccessGrant { RouteId = routeId, UserId = userId });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    /// <summary>Creates a system-realm group holding <paramref name="memberIds"/> and returns its id.</summary>
    public static Task<int> AddGroupAsync(
        this WatchtowerApiFactory factory, string name, params int[] memberIds) =>
        AddGroupInRealmAsync(factory, name, Realm.SystemRealmId, memberIds);

    /// <summary>Creates a group in <paramref name="realmId"/> holding <paramref name="memberIds"/>.</summary>
    public static async Task<int> AddGroupInRealmAsync(
        this WatchtowerApiFactory factory, string name, int realmId, params int[] memberIds) {
        var groupId = 0;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var ct = TestContext.Current.CancellationToken;

            var group = new Group {
                RealmId = realmId,
                Name = name,
                NormalizedName = name.ToUpperInvariant(),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Groups.Add(group);
            await db.SaveChangesAsync(ct);

            foreach (var userId in memberIds)
                db.GroupMembers.Add(new GroupMember { GroupId = group.Id, UserId = userId });
            await db.SaveChangesAsync(ct);
            groupId = group.Id;
        });
        return groupId;
    }

    /// <summary>Lets every member of <paramref name="groupId"/> through a <c>Restricted</c> route.</summary>
    public static Task GrantGroupAsync(this WatchtowerApiFactory factory, int routeId, int groupId) =>
        factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            db.RouteAccessGrants.Add(new RouteAccessGrant { RouteId = routeId, GroupId = groupId });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    /// <summary>Removes one account from one group, the way an administrator revoking membership does.</summary>
    public static Task RemoveFromGroupAsync(this WatchtowerApiFactory factory, int groupId, int userId) =>
        factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var ct = TestContext.Current.CancellationToken;
            var member = await db.GroupMembers.SingleAsync(m => m.GroupId == groupId && m.UserId == userId, ct);
            db.GroupMembers.Remove(member);
            await db.SaveChangesAsync(ct);
        });

    /// <summary>Mints an app session the way the callback does, and returns the raw cookie token.</summary>
    public static async Task<string> AppSessionAsync(this WatchtowerApiFactory factory, int userId, int routeId) {
        var token = string.Empty;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var sessions = sp.GetRequiredService<AuthSessionService>();
            var ct = TestContext.Current.CancellationToken;
            var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
            token = await sessions.CreateAppSessionAsync(user, routeId, ct);
        });
        return token;
    }

    /// <summary>Mints a central session and returns the raw cookie token.</summary>
    public static async Task<string> SsoSessionAsync(this WatchtowerApiFactory factory, int userId) {
        var token = string.Empty;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var sessions = sp.GetRequiredService<AuthSessionService>();
            var ct = TestContext.Current.CancellationToken;
            var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
            token = await sessions.CreateSsoSessionAsync(user, ct);
        });
        return token;
    }

    /// <summary>The audit rows written so far, oldest first.</summary>
    public static async Task<IReadOnlyList<AuditEvent>> AuditEventsAsync(this WatchtowerApiFactory factory) {
        IReadOnlyList<AuditEvent> events = [];
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            events = await db.AuditEvents.AsNoTracking().OrderBy(e => e.Id)
                .ToListAsync(TestContext.Current.CancellationToken);
        });
        return events;
    }
}
