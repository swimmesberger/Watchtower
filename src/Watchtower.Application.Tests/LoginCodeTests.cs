using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the one-time code that carries a central login across to a protected app's own domain, and the
/// per-app session it is exchanged for (docs/central-auth/design.md §4, §5).
/// </summary>
/// <remarks>
/// This is the only credential in the system that travels in a URL — through the address bar, the browser
/// history and any proxy log on the way. Single use, sixty seconds and route binding are what make that
/// acceptable, so each of them is asserted rather than assumed.
/// </remarks>
public sealed class LoginCodeTests {
    private const string Password = "correct-horse-battery";
    private const string AppUrl = "https://app.example.invalid/reports?range=30d";

    [Fact]
    public async Task MintedCode_StoresOnlyItsHash_AndRedeemsToWhatItWasBoundTo() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var userId = await CreateUserAsync(host, "alice");
        var routeId = await CreateRouteAsync(host, "app.example.invalid");

        string code;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            code = await sessions.MintLoginCodeAsync(userId, routeId, AppUrl, ct);

            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var stored = await db.LoginCodes.AsNoTracking().SingleAsync(ct);
            Assert.NotEqual(code, stored.CodeHash);
            Assert.Equal(AuthSessionService.HashToken(code), stored.CodeHash);
            Assert.Equal(host.Time.Now.AddSeconds(60), stored.ExpiresAt);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            var grant = await sessions.RedeemLoginCodeAsync(code, ct);

            Assert.NotNull(grant);
            Assert.Equal(userId, grant.UserId);
            Assert.Equal(routeId, grant.RouteId);
            Assert.Equal(AppUrl, grant.RedirectUri);
        }
    }

    [Fact]
    public async Task Code_IsSingleUse() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var userId = await CreateUserAsync(host, "alice");
        var routeId = await CreateRouteAsync(host, "app.example.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
        var code = await sessions.MintLoginCodeAsync(userId, routeId, AppUrl, ct);

        Assert.NotNull(await sessions.RedeemLoginCodeAsync(code, ct));
        // The row is gone with the first redemption, so a code captured from a log or a history entry buys
        // an attacker nothing once the legitimate browser has followed the redirect.
        Assert.Null(await sessions.RedeemLoginCodeAsync(code, ct));

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.LoginCodes.AnyAsync(ct));
    }

    [Fact]
    public async Task ExpiredCode_IsRefused_AndConsumed() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var userId = await CreateUserAsync(host, "alice");
        var routeId = await CreateRouteAsync(host, "app.example.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
        var code = await sessions.MintLoginCodeAsync(userId, routeId, AppUrl, ct);

        host.Time.Advance(TimeSpan.FromSeconds(61));

        Assert.Null(await sessions.RedeemLoginCodeAsync(code, ct));
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.LoginCodes.AnyAsync(ct));
    }

    [Fact]
    public async Task UnknownCode_IsRefused_WithoutTouchingTheOthers() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var userId = await CreateUserAsync(host, "alice");
        var routeId = await CreateRouteAsync(host, "app.example.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
        var code = await sessions.MintLoginCodeAsync(userId, routeId, AppUrl, ct);

        Assert.Null(await sessions.RedeemLoginCodeAsync(null, ct));
        Assert.Null(await sessions.RedeemLoginCodeAsync(string.Empty, ct));
        Assert.Null(await sessions.RedeemLoginCodeAsync("not-a-code", ct));
        Assert.Null(await sessions.RedeemLoginCodeAsync(code + "x", ct));

        Assert.NotNull(await sessions.RedeemLoginCodeAsync(code, ct));
    }

    [Fact]
    public async Task MintingACode_SweepsExpiredOnes() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var userId = await CreateUserAsync(host, "alice");
        var routeId = await CreateRouteAsync(host, "app.example.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
        await sessions.MintLoginCodeAsync(userId, routeId, AppUrl, ct);

        host.Time.Advance(TimeSpan.FromMinutes(5));
        await sessions.MintLoginCodeAsync(userId, routeId, AppUrl, ct);

        // Nothing sweeps codes in the background, so an abandoned redirect must not leave a row behind for
        // the lifetime of the deployment.
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var remaining = await db.LoginCodes.AsNoTracking().ToListAsync(ct);
        Assert.Single(remaining);
        Assert.Equal(host.Time.Now, remaining[0].CreatedAt);
    }

    [Fact]
    public async Task AppSession_IsBoundToItsRoute() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var userId = await CreateUserAsync(host, "alice");
        var appRoute = await CreateRouteAsync(host, "app.example.invalid");
        var otherRoute = await CreateRouteAsync(host, "other.example.invalid");

        string token;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
            token = await sessions.CreateAppSessionAsync(user, appRoute, ct);

            var stored = await db.AuthSessions.AsNoTracking().SingleAsync(ct);
            Assert.Equal(SessionKind.App, stored.Kind);
            Assert.Equal(appRoute, stored.RouteId);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();

            Assert.NotNull(await sessions.ValidateAppSessionAsync(token, appRoute, ct));
            // Another app's verify call must not accept it, and — because the two live on different hosts
            // — must not delete it either.
            Assert.Null(await sessions.ValidateAppSessionAsync(token, otherRoute, ct));
            // Nor is it a central session: the SSO cookie and the app cookie are not interchangeable.
            Assert.Null(await sessions.ValidateAsync(token, ct));
            Assert.NotNull(await sessions.ValidateAppSessionAsync(token, appRoute, ct));
        }
    }

    [Fact]
    public async Task PerAppLogout_RevokesOnlyThatApp() {
        using var host = AuthTestHost.Start();
        var ct = TestContext.Current.CancellationToken;
        var userId = await CreateUserAsync(host, "alice");
        var appRoute = await CreateRouteAsync(host, "app.example.invalid");
        var otherRoute = await CreateRouteAsync(host, "other.example.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);

        var ssoToken = await sessions.CreateSsoSessionAsync(user, ct);
        var appToken = await sessions.CreateAppSessionAsync(user, appRoute, ct);
        var otherToken = await sessions.CreateAppSessionAsync(user, otherRoute, ct);

        Assert.True(await sessions.RevokeAppSessionAsync(appToken, ct));

        Assert.Null(await sessions.ValidateAppSessionAsync(appToken, appRoute, ct));
        // Signing out of one app is not signing out of Watchtower, nor of the visitor's other apps.
        Assert.NotNull(await sessions.ValidateAppSessionAsync(otherToken, otherRoute, ct));
        Assert.NotNull(await sessions.ValidateAsync(ssoToken, ct));
        // And an SSO token presented to the per-app logout must not take the central session down with it.
        Assert.False(await sessions.RevokeAppSessionAsync(ssoToken, ct));
        Assert.NotNull(await sessions.ValidateAsync(ssoToken, ct));
    }

    private static async Task<int> CreateUserAsync(AuthTestHost host, string userName) {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = AuthTestHost.NewUser(userName);
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user.Id;
    }

    private static async Task<int> CreateRouteAsync(AuthTestHost host, string domain) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;

        var name = domain.Split('.')[0];
        var stack = new Stack {
            Name = name,
            ComposeProjectName = name,
            Product = TestProducts.New(name),
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(ct);

        var route = new Route {
            StackId = stack.Id,
            Domain = domain,
            ServiceName = "web",
            ContainerPort = 8080,
            AccessMode = AccessMode.Authenticated,
        };
        db.Routes.Add(route);
        await db.SaveChangesAsync(ct);
        return route.Id;
    }
}
