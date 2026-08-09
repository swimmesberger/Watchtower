using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>Covers first-run admin creation and the break-glass password reset (design.md §2.6, §11).</summary>
public sealed class AuthBootstrapServiceTests {
    private const string BootstrapPassword = "bootstrap-password";

    private static (string, string?) Enabled => ("Watchtower:Auth:Enabled", "true");
    private static (string, string?) Bootstrap => ("Watchtower:Auth:BootstrapPassword", BootstrapPassword);

    [Fact]
    public async Task DisabledByDefault_LeavesTheDatabaseEmpty() {
        using var host = AuthTestHost.Start();

        await host.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, await CountUsersAsync(host));
    }

    [Fact]
    public async Task EmptyDatabase_CreatesTheAdminAccount() {
        using var host = AuthTestHost.Start(Enabled, Bootstrap);

        await host.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);

        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var admin = await users.FindByNameAsync(AuthBootstrapService.AdminUserName);
        Assert.NotNull(admin);
        Assert.True(admin.IsAdmin);
        Assert.False(admin.Disabled);
        Assert.True(await users.CheckPasswordAsync(admin, BootstrapPassword));
    }

    [Fact]
    public async Task SecondStart_DoesNotCreateADuplicate() {
        using var host = AuthTestHost.Start(Enabled, Bootstrap);
        await host.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, await CountUsersAsync(host));

        using var restarted = host.Restart(Enabled, Bootstrap);
        await restarted.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, await CountUsersAsync(restarted));
    }

    [Fact]
    public async Task ResetPassword_ChangesThePasswordAndClearsTheLockout() {
        const string recoveryPassword = "break-glass-recovery";

        using var host = AuthTestHost.Start(Enabled, Bootstrap);
        await host.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);
        await LockOutAdminAsync(host);

        // Next start, with WATCHTOWER__AUTH__RESETPASSWORD set.
        using var recovered = host.Restart(Enabled, Bootstrap, ("Watchtower:Auth:ResetPassword", recoveryPassword));
        await recovered.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);

        await using var scope = recovered.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var admin = await users.FindByNameAsync(AuthBootstrapService.AdminUserName);
        Assert.NotNull(admin);
        Assert.False(await users.IsLockedOutAsync(admin));
        Assert.Equal(0, await users.GetAccessFailedCountAsync(admin));
        Assert.True(await users.CheckPasswordAsync(admin, recoveryPassword));
        Assert.False(await users.CheckPasswordAsync(admin, BootstrapPassword));
        // Still exactly one account — recovery resets, it does not add.
        Assert.Equal(1, await CountUsersAsync(recovered));
    }

    [Fact]
    public async Task ResetPassword_ThatViolatesThePolicy_LeavesTheOldPasswordWorking() {
        using var host = AuthTestHost.Start(Enabled, Bootstrap);
        await host.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);

        // "short" is below the 10-character minimum: the reset must be refused *before* anything is
        // written, or the operator is left with an account that has no usable password at all.
        using var attempted = host.Restart(Enabled, Bootstrap, ("Watchtower:Auth:ResetPassword", "short"));
        await attempted.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);

        await using var scope = attempted.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var admin = await users.FindByNameAsync(AuthBootstrapService.AdminUserName);
        Assert.NotNull(admin);
        Assert.NotEqual(string.Empty, admin.PasswordHash);
        Assert.True(await users.CheckPasswordAsync(admin, BootstrapPassword));
        Assert.False(await users.CheckPasswordAsync(admin, "short"));
    }

    [Fact]
    public async Task ResetPassword_RecreatesTheAdminAccount_WhenItWasDeleted() {
        const string recoveryPassword = "break-glass-recovery";

        using var host = AuthTestHost.Start(Enabled, Bootstrap);
        await host.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);

        // The operator renamed the seeded account away — but other users still exist, so the ordinary
        // first-run bootstrap will not step in.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var admin = await users.FindByNameAsync(AuthBootstrapService.AdminUserName);
            Assert.NotNull(admin);
            Assert.True((await users.SetUserNameAsync(admin, "operator")).Succeeded);
        }

        using var recovered = host.Restart(Enabled, Bootstrap, ("Watchtower:Auth:ResetPassword", recoveryPassword));
        await recovered.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);

        await using (var scope = recovered.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var admin = await users.FindByNameAsync(AuthBootstrapService.AdminUserName);
            Assert.NotNull(admin);
            Assert.True(admin.IsAdmin);
            Assert.True(await users.CheckPasswordAsync(admin, recoveryPassword));
        }

        // The renamed account is untouched; recovery added one, it did not replace anything.
        Assert.Equal(2, await CountUsersAsync(recovered));
    }

    /// <summary>
    /// Leaves the admin account both locked out <em>and</em> carrying a non-zero failure count.
    /// Stopping one attempt short of the threshold matters: Identity zeroes
    /// <c>AccessFailedCount</c> on the attempt that trips the lockout, so a 5-failure setup would make
    /// the post-reset "count is zero" assertion pass no matter what the reset did.
    /// </summary>
    private static async Task LockOutAdminAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var admin = await users.FindByNameAsync(AuthBootstrapService.AdminUserName);
        Assert.NotNull(admin);

        for (var attempt = 1; attempt <= 4; attempt++)
            await users.AccessFailedAsync(admin);
        Assert.Equal(4, await users.GetAccessFailedCountAsync(admin));

        await users.SetLockoutEndDateAsync(admin, DateTimeOffset.UtcNow.AddMinutes(15));
        Assert.True(await users.IsLockedOutAsync(admin));
    }

    private static async Task<int> CountUsersAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Users.CountAsync(TestContext.Current.CancellationToken);
    }
}
