using System.Security.Claims;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Users;
using Watchtower.Application.Modules.Users.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the Users module (docs/central-auth/design.md §7) through its real generated handler
/// pipelines, so the <c>[RequireRole("Admin")]</c> decorator and the module's own rules are exercised
/// together — the guard against an instance losing its last administrator, the session revocation that
/// makes a password reset or a suspension actually take effect, and the audit trail.
/// </summary>
public sealed class UsersModuleTests {
    private const string GoodPassword = "correct-horse-battery";
    private const string OtherPassword = "another-good-passphrase";

    /// <summary>Every Users handler, added to the test host the way the generated module registration does.</summary>
    private static readonly Action<IServiceCollection> WithUsersModule = services => {
        services.AddListUsers();
        services.AddCreateUser();
        services.AddUpdateUser();
        services.AddResetUserPassword();
        services.AddSetUserDisabled();
        services.AddDeleteUser();
    };

    // -- Listing ---------------------------------------------------------------------------------

    [Fact]
    public async Task List_WithAuthDisabled_IsAllowedForTheImplicitLocalAdministrator() {
        using var host = AuthTestHost.Start(WithUsersModule);
        await SeedUserAsync(host, "alice", isAdmin: true);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListUsers.Query, ListUsers.Response>(
            scope.ServiceProvider, new ListUsers.Query());

        Assert.True(result.IsSuccess, Describe(result));
        var alice = Assert.Single(result.Value.Users);
        Assert.Equal("alice", alice.UserName);
        Assert.True(alice.IsAdmin);
        Assert.False(alice.Disabled);
        Assert.False(alice.LockedOut);
    }

    [Fact]
    public async Task List_WithAuthEnabled_IsDeniedToANonAdministrator() {
        using var host = AuthTestHost.Start(WithUsersModule, ("Watchtower:Auth:Enabled", "true"));

        await using var scope = host.Services.CreateAsyncScope();
        SeedPrincipal(scope.ServiceProvider, isAdmin: false);
        var result = await SendAsync<ListUsers.Query, ListUsers.Response>(
            scope.ServiceProvider, new ListUsers.Query());

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Forbidden, result.Error.Kind);
    }

    [Fact]
    public async Task List_ReportsALockedOutAccountUntilTheLockoutLapses() {
        using var host = AuthTestHost.Start(WithUsersModule);
        var id = await SeedUserAsync(host, "alice", isAdmin: false);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var alice = await users.FindByIdAsync(id.ToString());
            Assert.NotNull(alice);
            await users.SetLockoutEndDateAsync(alice, host.Time.Now.AddMinutes(15));
        }

        Assert.True((await ListAsync(host)).Single().LockedOut);

        host.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.False((await ListAsync(host)).Single().LockedOut);
    }

    // -- Create ----------------------------------------------------------------------------------

    [Fact]
    public async Task Create_HashesThePassword_AndAudits() {
        using var host = AuthTestHost.Start(WithUsersModule);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateUser.Command, CreateUser.Response>(
            scope.ServiceProvider, new CreateUser.Command("alice", GoodPassword, "alice@example.com", IsAdmin: true));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("alice", result.Value.User.UserName);
        Assert.Equal("alice@example.com", result.Value.User.Email);
        Assert.True(result.Value.User.IsAdmin);

        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var alice = await users.FindByNameAsync("alice");
        Assert.NotNull(alice);
        Assert.NotEqual(GoodPassword, alice.PasswordHash);
        Assert.True(await users.CheckPasswordAsync(alice, GoodPassword));

        Assert.Equal(["user.created"], await AuditKindsAsync(host));
    }

    [Fact]
    public async Task Create_MapsADuplicateNameToAValidationFailure() {
        using var host = AuthTestHost.Start(WithUsersModule);
        await SeedUserAsync(host, "alice", isAdmin: false);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateUser.Command, CreateUser.Response>(
            scope.ServiceProvider, new CreateUser.Command("ALICE", GoodPassword, null, IsAdmin: false));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task Create_RejectsAPasswordThePolicyDoesNotAllow() {
        using var host = AuthTestHost.Start(WithUsersModule);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateUser.Command, CreateUser.Response>(
            scope.ServiceProvider, new CreateUser.Command("alice", "short", null, IsAdmin: false));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Empty(await ListAsync(host));
    }

    // -- Last-admin guard ------------------------------------------------------------------------

    [Fact]
    public async Task Update_RefusesToDemoteTheLastAdministrator() {
        using var host = AuthTestHost.Start(WithUsersModule);
        var id = await SeedUserAsync(host, "admin", isAdmin: true);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<UpdateUser.Command, UpdateUser.Response>(
            scope.ServiceProvider, new UpdateUser.Command(id, "admin", null, IsAdmin: false));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.BusinessRule, result.Error.Kind);
        Assert.True((await ListAsync(host)).Single().IsAdmin);
    }

    [Fact]
    public async Task Update_AllowsDemotionOnceASecondAdministratorExists() {
        using var host = AuthTestHost.Start(WithUsersModule);
        var id = await SeedUserAsync(host, "admin", isAdmin: true);
        await SeedUserAsync(host, "second", isAdmin: true);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<UpdateUser.Command, UpdateUser.Response>(
            scope.ServiceProvider, new UpdateUser.Command(id, "admin", "admin@example.com", IsAdmin: false));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.False(result.Value.User.IsAdmin);
        Assert.Equal("admin@example.com", result.Value.User.Email);
    }

    [Fact]
    public async Task Update_IgnoresADisabledAdministratorWhenCountingTheRemainingOnes() {
        using var host = AuthTestHost.Start(WithUsersModule);
        var id = await SeedUserAsync(host, "admin", isAdmin: true);
        var spare = await SeedUserAsync(host, "spare", isAdmin: true);
        await SetDisabledAsync(host, spare, disabled: true);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<UpdateUser.Command, UpdateUser.Response>(
            scope.ServiceProvider, new UpdateUser.Command(id, "admin", null, IsAdmin: false));

        // A disabled administrator cannot sign in, so it does not count as the remaining one.
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.BusinessRule, result.Error.Kind);
    }

    [Fact]
    public async Task SetDisabled_RefusesToDisableTheLastAdministrator() {
        using var host = AuthTestHost.Start(WithUsersModule);
        var id = await SeedUserAsync(host, "admin", isAdmin: true);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<SetUserDisabled.Command, SetUserDisabled.Response>(
            scope.ServiceProvider, new SetUserDisabled.Command(id, Disabled: true));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.BusinessRule, result.Error.Kind);
        Assert.False((await ListAsync(host)).Single().Disabled);
    }

    [Fact]
    public async Task Delete_RefusesToRemoveTheLastAdministrator() {
        using var host = AuthTestHost.Start(WithUsersModule);
        var id = await SeedUserAsync(host, "admin", isAdmin: true);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<DeleteUser.Command, DeleteUser.Response>(
            scope.ServiceProvider, new DeleteUser.Command(id));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.BusinessRule, result.Error.Kind);
        Assert.Single(await ListAsync(host));
    }

    [Fact]
    public async Task Delete_AllowsRemovingANonAdministratorWithNoAdministratorsLeftToLose() {
        using var host = AuthTestHost.Start(WithUsersModule);
        await SeedUserAsync(host, "admin", isAdmin: true);
        var id = await SeedUserAsync(host, "bob", isAdmin: false);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<DeleteUser.Command, DeleteUser.Response>(
            scope.ServiceProvider, new DeleteUser.Command(id));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("admin", (await ListAsync(host)).Single().UserName);
    }

    // -- Password reset --------------------------------------------------------------------------

    [Fact]
    public async Task ResetPassword_ChangesThePassword_AndRevokesEverySession() {
        using var host = AuthTestHost.Start(WithUsersModule);
        var id = await SeedUserAsync(host, "alice", isAdmin: true);
        await CreateSessionAsync(host, id);
        Assert.Equal(1, await SessionCountAsync(host, id));

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<ResetUserPassword.Command, ResetUserPassword.Response>(
                scope.ServiceProvider, new ResetUserPassword.Command(id, OtherPassword));
            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal(id, result.Value.Id);
        }

        Assert.Equal(0, await SessionCountAsync(host, id));

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var alice = await users.FindByIdAsync(id.ToString());
            Assert.NotNull(alice);
            Assert.True(await users.CheckPasswordAsync(alice, OtherPassword));
            Assert.False(await users.CheckPasswordAsync(alice, GoodPassword));
        }

        Assert.Contains("user.password.reset", await AuditKindsAsync(host));
    }

    [Fact]
    public async Task ResetPassword_RejectsAPolicyViolation_LeavingTheOldPasswordAndSessionsIntact() {
        using var host = AuthTestHost.Start(WithUsersModule);
        var id = await SeedUserAsync(host, "alice", isAdmin: true);
        await CreateSessionAsync(host, id);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<ResetUserPassword.Command, ResetUserPassword.Response>(
                scope.ServiceProvider, new ResetUserPassword.Command(id, "short"));
            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var alice = await users.FindByIdAsync(id.ToString());
            Assert.NotNull(alice);
            // The reset token runs the validators before the hash is touched, so the old password stands.
            Assert.True(await users.CheckPasswordAsync(alice, GoodPassword));
        }

        Assert.Equal(1, await SessionCountAsync(host, id));
        Assert.Empty(await AuditKindsAsync(host));
    }

    [Fact]
    public async Task ResetPassword_ReportsAnUnknownAccountAsNotFound() {
        using var host = AuthTestHost.Start(WithUsersModule);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ResetUserPassword.Command, ResetUserPassword.Response>(
            scope.ServiceProvider, new ResetUserPassword.Command(404, OtherPassword));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    // -- Disable / enable ------------------------------------------------------------------------

    [Fact]
    public async Task SetDisabled_RevokesSessions_AndReEnablingClearsTheLockout() {
        using var host = AuthTestHost.Start(WithUsersModule);
        await SeedUserAsync(host, "admin", isAdmin: true);
        var id = await SeedUserAsync(host, "bob", isAdmin: true);
        await CreateSessionAsync(host, id);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var bob = await users.FindByIdAsync(id.ToString());
            Assert.NotNull(bob);
            await users.SetLockoutEndDateAsync(bob, host.Time.Now.AddHours(1));
            await users.AccessFailedAsync(bob);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var disabled = await SendAsync<SetUserDisabled.Command, SetUserDisabled.Response>(
                scope.ServiceProvider, new SetUserDisabled.Command(id, Disabled: true));
            Assert.True(disabled.IsSuccess, Describe(disabled));
            Assert.True(disabled.Value.User.Disabled);
        }

        Assert.Equal(0, await SessionCountAsync(host, id));

        await using (var scope = host.Services.CreateAsyncScope()) {
            var enabled = await SendAsync<SetUserDisabled.Command, SetUserDisabled.Response>(
                scope.ServiceProvider, new SetUserDisabled.Command(id, Disabled: false));
            Assert.True(enabled.IsSuccess, Describe(enabled));
            Assert.False(enabled.Value.User.Disabled);
            // Re-enabling has to produce a usable account, not one still parked behind a lockout.
            Assert.False(enabled.Value.User.LockedOut);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var bob = await users.FindByIdAsync(id.ToString());
            Assert.NotNull(bob);
            Assert.Null(bob.LockoutEnd);
            Assert.Equal(0, bob.AccessFailedCount);
            Assert.False(await users.IsLockedOutAsync(bob));
        }

        Assert.Equal(["user.disabled", "user.enabled"], await AuditKindsAsync(host));
    }

    // -- Delete ----------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesTheAccountAndItsSessions_AndKeepsTheAuditRow() {
        using var host = AuthTestHost.Start(WithUsersModule);
        await SeedUserAsync(host, "admin", isAdmin: true);
        var id = await SeedUserAsync(host, "bob", isAdmin: false);
        await CreateSessionAsync(host, id);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<DeleteUser.Command, DeleteUser.Response>(
                scope.ServiceProvider, new DeleteUser.Command(id));
            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal(id, result.Value.Id);
        }

        Assert.Equal(0, await SessionCountAsync(host, id));

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.Users.AnyAsync(u => u.Id == id, TestContext.Current.CancellationToken));

            // The trail outlives its subject: the FK detaches (SET NULL), so the name lives in Detail.
            var deleted = await db.AuthEvents
                .SingleAsync(e => e.Kind == "user.deleted", TestContext.Current.CancellationToken);
            Assert.Null(deleted.UserId);
            Assert.Contains($"target=bob#{id}", deleted.Detail);
        }
    }

    [Fact]
    public async Task Delete_ReportsAnUnknownAccountAsNotFound() {
        using var host = AuthTestHost.Start(WithUsersModule);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<DeleteUser.Command, DeleteUser.Response>(
            scope.ServiceProvider, new DeleteUser.Command(404));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    // -- Helpers ---------------------------------------------------------------------------------

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>()
            .HandleAsync(request, TestContext.Current.CancellationToken);

    private static async Task<int> SeedUserAsync(AuthTestHost host, string userName, bool isAdmin) {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = AuthTestHost.NewUser(userName);
        user.IsAdmin = isAdmin;
        var created = await users.CreateAsync(user, GoodPassword);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user.Id;
    }

    private static async Task SetDisabledAsync(AuthTestHost host, int userId, bool disabled) {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.FindByIdAsync(userId.ToString());
        Assert.NotNull(user);
        user.Disabled = disabled;
        Assert.True((await users.UpdateAsync(user)).Succeeded);
    }

    private static async Task CreateSessionAsync(AuthTestHost host, int userId) {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
        var user = await users.FindByIdAsync(userId.ToString());
        Assert.NotNull(user);
        await sessions.CreateSsoSessionAsync(user, TestContext.Current.CancellationToken);
    }

    private static async Task<int> SessionCountAsync(AuthTestHost host, int userId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuthSessions.CountAsync(s => s.UserId == userId, TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<string>> AuditKindsAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuthEvents.OrderBy(e => e.Id)
            .Select(e => e.Kind)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<IReadOnlyList<UserDto>> ListAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListUsers.Query, ListUsers.Response>(
            scope.ServiceProvider, new ListUsers.Query());
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value.Users;
    }

    /// <summary>Applies a principal the way every Elarion transport does — through the dispatch-scope rail.</summary>
    private static void SeedPrincipal(IServiceProvider scope, bool isAdmin) {
        var claims = new List<Claim> {
            new(WatchtowerClaims.UserId, "7"),
            new(WatchtowerClaims.Name, "caller"),
        };
        if (isAdmin) claims.Add(new Claim(WatchtowerClaims.Role, WatchtowerClaims.AdminRole));

        var context = new DispatchScopeContext();
        context.Set(new ClaimsPrincipal(new ClaimsIdentity(
            claims, "WatchtowerSession", WatchtowerClaims.Name, WatchtowerClaims.Role)));
        scope.SeedScope(context);
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
