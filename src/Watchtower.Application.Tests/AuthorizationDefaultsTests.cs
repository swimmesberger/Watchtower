using System.Security.Claims;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Elarion.Abstractions.Identity;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Modules.Credentials.Handlers;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the blast radius of <c>[assembly: ElarionAuthorizationDefaults]</c> (docs/central-auth/design.md
/// §2.5, §11): every handler now requires an authenticated principal, so the two things that must hold are
/// that the no-auth deployment is unchanged and that the authenticated one actually denies strangers.
/// </summary>
/// <remarks>
/// Dispatch goes through a real generated handler pipeline (<see cref="ListCredentials"/> — the cheapest
/// one, it needs nothing but the context) rather than through <c>IAuthorizer</c> directly, so the
/// generator's decision to attach the decorator is part of what is being asserted.
/// </remarks>
public sealed class AuthorizationDefaultsTests {
    private static readonly Action<IServiceCollection> WithListCredentials =
        services => services.AddListCredentials();

    [Fact]
    public void AuthDisabled_TheCurrentUserIsAnImplicitLocalAdministrator() {
        using var host = AuthTestHost.Start();

        var user = host.Services.GetRequiredService<ICurrentUser>();

        Assert.IsType<ImplicitAdminCurrentUser>(user);
        Assert.True(user.IsAuthenticated);
        Assert.Equal(ImplicitAdminCurrentUser.LocalUserId, user.UserId);
        Assert.True(user.IsInRole(WatchtowerClaims.AdminRole));
        Assert.False(user.IsInRole("SomethingElse"));
    }

    [Fact]
    public async Task AuthDisabled_HandlersStillDispatch() {
        using var host = AuthTestHost.Start(WithListCredentials);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await DispatchAsync(scope.ServiceProvider);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value.Credentials);
    }

    [Fact]
    public async Task AuthEnabled_AnAnonymousCallerIsUnauthorized() {
        using var host = AuthTestHost.Start(WithListCredentials, ("Watchtower:Auth:Enabled", "true"));

        await using var scope = host.Services.CreateAsyncScope();
        var result = await DispatchAsync(scope.ServiceProvider);

        Assert.False(result.IsSuccess);
        // ErrorKind.Unauthorized is what the JSON-RPC transport maps to -32005, which is the code the
        // frontend turns into a redirect to the login page.
        Assert.Equal(ErrorKind.Unauthorized, result.Error.Kind);
    }

    [Fact]
    public async Task AuthEnabled_ASignedInCallerDispatches() {
        using var host = AuthTestHost.Start(WithListCredentials, ("Watchtower:Auth:Enabled", "true"));

        await using var scope = host.Services.CreateAsyncScope();
        SeedPrincipal(scope.ServiceProvider, NewPrincipal("7", "alice", isAdmin: true));

        var result = await DispatchAsync(scope.ServiceProvider);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void AuthEnabled_TheClaimsSnapshotMapsTheHandlersClaimTypes() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Enabled", "true"));

        using var scope = host.Services.CreateScope();
        SeedPrincipal(scope.ServiceProvider, NewPrincipal("7", "alice", isAdmin: true, email: "alice@example.com"));
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        Assert.IsType<WatchtowerClaimsCurrentUser>(user);
        Assert.True(user.IsAuthenticated);
        Assert.Equal("7", user.UserId);
        Assert.Equal("alice@example.com", user.Email);
        Assert.True(user.IsInRole(WatchtowerClaims.AdminRole));
    }

    [Fact]
    public void AuthEnabled_AnAnonymousCallerHasAnEmptyUserIdRatherThanThrowing() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Enabled", "true"));

        using var scope = host.Services.CreateScope();
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        Assert.False(user.IsAuthenticated);
        // The elarion.session bootstrap reads UserId unconditionally, and an anonymous browser calls it
        // before it can even reach the login page — throwing here would 500 the SPA's boot request.
        Assert.Equal(string.Empty, user.UserId);
        Assert.Empty(user.Roles);
    }

    [Fact]
    public void AuthEnabled_ANonAdminAccountDoesNotHoldTheAdminRole() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Enabled", "true"));

        using var scope = host.Services.CreateScope();
        SeedPrincipal(scope.ServiceProvider, NewPrincipal("9", "bob", isAdmin: false));
        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        Assert.True(user.IsAuthenticated);
        Assert.False(user.IsInRole(WatchtowerClaims.AdminRole));
    }

    private static ValueTask<Result<ListCredentials.Response>> DispatchAsync(IServiceProvider scope) =>
        scope.GetRequiredService<IHandler<ListCredentials.Query, Result<ListCredentials.Response>>>()
            .HandleAsync(new ListCredentials.Query(), TestContext.Current.CancellationToken);

    /// <summary>Applies a principal the way every Elarion transport does — through the dispatch-scope rail.</summary>
    private static void SeedPrincipal(IServiceProvider scope, ClaimsPrincipal principal) =>
        TestPrincipal.Seed(scope, principal);

    /// <summary>
    /// The principal <c>WatchtowerSessionAuthenticationHandler</c> mints, rebuilt from the same
    /// <see cref="WatchtowerClaims"/> constants — which is the point of those constants existing.
    /// </summary>
    private static ClaimsPrincipal NewPrincipal(string id, string name, bool isAdmin, string? email = null) =>
        TestPrincipal.New(id, name, isAdmin, email);
}
