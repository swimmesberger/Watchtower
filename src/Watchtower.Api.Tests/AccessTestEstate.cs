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

    /// <summary>Adds a stack and a route for <paramref name="domain"/> and returns the route id.</summary>
    public static async Task<int> AddRouteAsync(
        this WatchtowerApiFactory factory, string domain, AccessMode mode, string? bypassPaths = null,
        IdentityHeaderMode identityHeaderMode = IdentityHeaderMode.None) {
        var routeId = 0;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
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
                IdentityHeaderMode = identityHeaderMode,
                BypassPaths = bypassPaths,
            };
            db.Routes.Add(route);
            await db.SaveChangesAsync(ct);
            routeId = route.Id;
        });
        return routeId;
    }

    /// <summary>Creates an account through <c>UserManager</c> and returns its id.</summary>
    public static async Task<int> AddUserAsync(
        this WatchtowerApiFactory factory, string userName, string? email = null, bool disabled = false) {
        var userId = 0;
        await factory.WithScopeAsync(async sp => {
            var users = sp.GetRequiredService<UserManager<User>>();
            var user = new User {
                UserName = userName,
                NormalizedUserName = userName.ToUpperInvariant(),
                Email = email,
                Disabled = disabled,
                PasswordHash = string.Empty,
                SecurityStamp = string.Empty,
                ConcurrencyStamp = string.Empty,
            };
            var created = await users.CreateAsync(user, Password);
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
    public static async Task<IReadOnlyList<AuthEvent>> AuthEventsAsync(this WatchtowerApiFactory factory) {
        IReadOnlyList<AuthEvent> events = [];
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            events = await db.AuthEvents.AsNoTracking().OrderBy(e => e.Id)
                .ToListAsync(TestContext.Current.CancellationToken);
        });
        return events;
    }
}
