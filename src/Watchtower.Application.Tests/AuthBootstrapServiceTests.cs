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
    public async Task ResetPassword_RevokesTheAccountsExistingSessions() {
        using var host = AuthTestHost.Start(Enabled, Bootstrap);
        await host.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);

        string token;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            var admin = await users.FindByNameAsync(AuthBootstrapService.AdminUserName);
            token = await sessions.CreateSsoSessionAsync(admin!, TestContext.Current.CancellationToken);
        }

        // Break-glass is reached for when control of the account is in doubt. A session minted before the
        // reset would otherwise keep working for the whole absolute lifetime, so changing the password
        // would not actually have taken anyone's access away.
        using var recovered = host.Restart(Enabled, Bootstrap, ("Watchtower:Auth:ResetPassword", "break-glass-recovery"));
        await recovered.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);

        await using (var scope = recovered.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            Assert.Null(await sessions.ValidateAsync(token, TestContext.Current.CancellationToken));

            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.AuthSessions.AnyAsync(TestContext.Current.CancellationToken));
        }
    }

    /// <summary>
    /// Break-glass clears the second factor as well as the password, and it has to: the commonest reason an
    /// operator reaches for this hook is a lost authenticator, and a recovery that restored the password but
    /// still demanded a code from a phone that is gone would restore nothing at all.
    /// </summary>
    /// <remarks>
    /// The trade is not hidden. Anyone who can set <c>WATCHTOWER__AUTH__RESETPASSWORD</c> and restart the
    /// process already owns the deployment, so the second factor was never a barrier to them — only to the
    /// account's legitimate owner. Both the audit row and the warning log say the factor was removed, which
    /// is what keeps the recovery from being silent.
    /// </remarks>
    [Fact]
    public async Task ResetPassword_AlsoClearsTheSecondFactor() {
        const string recoveryPassword = "break-glass-recovery";
        var ct = TestContext.Current.CancellationToken;

        using var host = AuthTestHost.Start(Enabled, Bootstrap);
        await host.CreateBootstrapService().StartAsync(ct);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var mfa = scope.ServiceProvider.GetRequiredService<UserMfaService>();
            var admin = await users.FindByNameAsync(AuthBootstrapService.AdminUserName);
            Assert.NotNull(admin);

            var enrolment = await mfa.BeginTotpAsync(admin, ct);
            Assert.NotNull(enrolment);
            var confirmed = await mfa.ConfirmTotpAsync(admin, TotpCodes.Current(enrolment.SharedKey), ct);
            Assert.Equal(UserMfaService.ConfirmOutcome.Enabled, confirmed.Outcome);
        }

        using var recovered = host.Restart(Enabled, Bootstrap, ("Watchtower:Auth:ResetPassword", recoveryPassword));
        await recovered.CreateBootstrapService().StartAsync(ct);

        await using (var scope = recovered.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var admin = await db.Users.SingleAsync(
                u => u.UserName == AuthBootstrapService.AdminUserName, ct);

            // All three, so the next login is password-only — which is the whole point of the hook.
            Assert.False(admin.TwoFactorEnabled);
            Assert.Null(admin.AuthenticatorKey);
            Assert.False(await db.UserRecoveryCodes.AnyAsync(c => c.UserId == admin.Id, ct));

            // The trail says so rather than leaving the removal to be inferred from the account's state.
            var row = await db.AuthEvents.SingleAsync(e => e.Kind == "auth.breakglass", ct);
            Assert.Contains("cleared two-factor enrolment", row.Detail);
        }

        await using (var scope = recovered.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var admin = await users.FindByNameAsync(AuthBootstrapService.AdminUserName);
            Assert.NotNull(admin);
            Assert.True(await users.CheckPasswordAsync(admin, recoveryPassword));
            Assert.False(await users.GetTwoFactorEnabledAsync(admin));
        }
    }

    /// <summary>An account with nothing enrolled is unaffected, and the row says that rather than implying a removal.</summary>
    [Fact]
    public async Task ResetPassword_OnAnAccountWithoutASecondFactor_SaysThereWasNothingToClear() {
        var ct = TestContext.Current.CancellationToken;

        using var host = AuthTestHost.Start(Enabled, Bootstrap);
        await host.CreateBootstrapService().StartAsync(ct);

        using var recovered = host.Restart(Enabled, Bootstrap, ("Watchtower:Auth:ResetPassword", "break-glass-recovery"));
        await recovered.CreateBootstrapService().StartAsync(ct);

        await using var scope = recovered.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuthEvents.SingleAsync(e => e.Kind == "auth.breakglass", ct);
        Assert.Contains("no two-factor enrolment to clear", row.Detail);
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

    [Fact]
    public async Task ResetPassword_LeavesABreakGlassAuditRow_WhereOrdinaryBootstrapDoesNot() {
        using var host = AuthTestHost.Start(Enabled, Bootstrap);
        await host.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);

        // First run created the admin, but touched no break-glass hook — so no such row yet.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.AuthEvents
                .AnyAsync(e => e.Kind == AuthEventKinds.BreakGlass, TestContext.Current.CancellationToken));
        }

        using var recovered = host.Restart(Enabled, Bootstrap, ("Watchtower:Auth:ResetPassword", "break-glass-recovery"));
        await recovered.CreateBootstrapService().StartAsync(TestContext.Current.CancellationToken);

        // An out-of-band recovery must leave a row in the trail, not only a log line.
        await using (var scope = recovered.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var admin = await users.FindByNameAsync(AuthBootstrapService.AdminUserName);
            var row = await db.AuthEvents
                .SingleAsync(e => e.Kind == AuthEventKinds.BreakGlass, TestContext.Current.CancellationToken);
            Assert.Equal(admin!.Id, row.UserId);
            Assert.False(string.IsNullOrWhiteSpace(row.Detail));
        }
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
