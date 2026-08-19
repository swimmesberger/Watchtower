using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the two-factor half of <see cref="WatchtowerUserStore"/> through the real
/// <see cref="UserManager{TUser}"/> — the layer that decides whether a stolen database backup is a set of
/// working credentials or a set of hashes, and whether a recovery code can be spent twice.
/// </summary>
public sealed class UserMfaStoreTests {
    private const string GoodPassword = "correct-horse-battery";

    // -- Authenticator key -----------------------------------------------------------------------

    [Fact]
    public async Task ResetAuthenticatorKey_StoresABase32KeyAndRotatesTheSecurityStamp() {
        using var host = AuthTestHost.Start();
        var id = await SeedUserAsync(host, "alice");

        string? key;
        string stampBefore, stampAfter;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var alice = await users.FindByIdAsync(id.ToString());
            Assert.NotNull(alice);
            stampBefore = alice.SecurityStamp;

            Assert.True((await users.ResetAuthenticatorKeyAsync(alice)).Succeeded);
            key = await users.GetAuthenticatorKeyAsync(alice);
            stampAfter = alice.SecurityStamp;
        }

        Assert.False(string.IsNullOrEmpty(key));
        // Base32, so an authenticator app can take it as typed.
        Assert.All(key!, c => Assert.Contains(c, "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"));
        // Enrolling rotates the stamp. Recorded, not enforced: session validation does not compare it
        // (see User.SecurityStamp), so nobody is signed out by this — the assertion pins that the value a
        // future stamp-validation hook will read is being maintained.
        Assert.NotEqual(stampBefore, stampAfter);

        // …and it survived the round trip rather than only living on the tracked entity.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var stored = await db.Users.SingleAsync(u => u.Id == id, TestContext.Current.CancellationToken);
            Assert.Equal(key, stored.AuthenticatorKey);
            Assert.Equal(stampAfter, stored.SecurityStamp);
            // The key alone must not switch two-factor on — that takes a proven code.
            Assert.False(stored.TwoFactorEnabled);
        }
    }

    /// <summary>
    /// The end-to-end proof that Identity's authenticator provider is registered and reading Watchtower's
    /// stored key: a code computed independently from that key (<see cref="TotpCodes"/>) verifies, and the
    /// adjacent code does not.
    /// </summary>
    [Fact]
    public async Task VerifyTwoFactorToken_AcceptsACodeComputedFromTheStoredKey() {
        using var host = AuthTestHost.Start();
        var id = await SeedUserAsync(host, "alice");

        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var alice = await users.FindByIdAsync(id.ToString());
        Assert.NotNull(alice);
        Assert.True((await users.ResetAuthenticatorKeyAsync(alice)).Succeeded);
        var key = await users.GetAuthenticatorKeyAsync(alice);
        Assert.NotNull(key);

        Assert.True(await users.VerifyTwoFactorTokenAsync(
            alice, TokenOptions.DefaultAuthenticatorProvider, TotpCodes.Current(key)));
        Assert.False(await users.VerifyTwoFactorTokenAsync(
            alice, TokenOptions.DefaultAuthenticatorProvider, TotpCodes.Wrong(key)));
    }

    [Fact]
    public async Task SetTwoFactorEnabled_PersistsAndRotatesTheSecurityStamp() {
        using var host = AuthTestHost.Start();
        var id = await SeedUserAsync(host, "alice");

        string before, after;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var alice = await users.FindByIdAsync(id.ToString());
            Assert.NotNull(alice);
            before = alice.SecurityStamp;
            Assert.True((await users.SetTwoFactorEnabledAsync(alice, true)).Succeeded);
            after = alice.SecurityStamp;
        }

        Assert.NotEqual(before, after);
        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var alice = await users.FindByIdAsync(id.ToString());
            Assert.NotNull(alice);
            Assert.True(await users.GetTwoFactorEnabledAsync(alice));
        }
    }

    // -- Recovery codes --------------------------------------------------------------------------

    [Fact]
    public async Task RecoveryCodes_AreStoredOnlyAsHashes() {
        using var host = AuthTestHost.Start();
        var id = await SeedUserAsync(host, "alice");

        var codes = await GenerateCodesAsync(host, id, count: 10);
        Assert.Equal(10, codes.Count);
        Assert.Equal(10, codes.Distinct(StringComparer.Ordinal).Count());

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stored = await db.UserRecoveryCodes
            .Where(c => c.UserId == id)
            .Select(c => c.CodeHash)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(10, stored.Count);
        // The stored value is the hash of the code, and the code itself appears nowhere: a database read
        // must not yield a credential that can be replayed at the login endpoint.
        Assert.Equal(
            codes.Select(AuthSessionService.HashToken).Order(StringComparer.Ordinal),
            stored.Order(StringComparer.Ordinal));
        foreach (var code in codes) Assert.DoesNotContain(code, stored);
    }

    [Fact]
    public async Task RecoveryCode_IsRedeemableExactlyOnce_AndCountsDown() {
        using var host = AuthTestHost.Start();
        var id = await SeedUserAsync(host, "alice");
        var codes = await GenerateCodesAsync(host, id, count: 10);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var alice = await users.FindByIdAsync(id.ToString());
            Assert.NotNull(alice);
            Assert.Equal(10, await users.CountRecoveryCodesAsync(alice));

            Assert.True((await users.RedeemTwoFactorRecoveryCodeAsync(alice, codes[0])).Succeeded);
            Assert.Equal(9, await users.CountRecoveryCodesAsync(alice));

            // Spent means gone: the row is deleted, so the second attempt has nothing to match.
            Assert.False((await users.RedeemTwoFactorRecoveryCodeAsync(alice, codes[0])).Succeeded);
            Assert.Equal(9, await users.CountRecoveryCodesAsync(alice));

            // An invented code is refused without disturbing the set.
            Assert.False((await users.RedeemTwoFactorRecoveryCodeAsync(alice, "AAAAA-BBBBB")).Succeeded);
            Assert.Equal(9, await users.CountRecoveryCodesAsync(alice));
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var spent = AuthSessionService.HashToken(codes[0]);
            Assert.False(await db.UserRecoveryCodes.AnyAsync(
                c => c.UserId == id && c.CodeHash == spent, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Regenerating_ReplacesTheWholeSet_SoTheOldCodesStopWorking() {
        using var host = AuthTestHost.Start();
        var id = await SeedUserAsync(host, "alice");
        var first = await GenerateCodesAsync(host, id, count: 10);
        var second = await GenerateCodesAsync(host, id, count: 10);

        Assert.Empty(first.Intersect(second, StringComparer.Ordinal));

        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var alice = await users.FindByIdAsync(id.ToString());
        Assert.NotNull(alice);
        Assert.Equal(10, await users.CountRecoveryCodesAsync(alice));
        Assert.False((await users.RedeemTwoFactorRecoveryCodeAsync(alice, first[0])).Succeeded);
        Assert.True((await users.RedeemTwoFactorRecoveryCodeAsync(alice, second[0])).Succeeded);
    }

    [Fact]
    public async Task DeletingTheAccount_TakesItsRecoveryCodesWithIt() {
        using var host = AuthTestHost.Start();
        var id = await SeedUserAsync(host, "alice");
        await GenerateCodesAsync(host, id, count: 10);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var alice = await users.FindByIdAsync(id.ToString());
            Assert.NotNull(alice);
            Assert.True((await users.DeleteAsync(alice)).Succeeded);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            // Nothing cleaned these up by hand — the FK cascade did.
            Assert.False(await db.UserRecoveryCodes.AnyAsync(TestContext.Current.CancellationToken));
        }
    }

    // -- The service over the store --------------------------------------------------------------

    [Fact]
    public async Task Disable_ClearsTheFlagTheKeyAndEveryCode() {
        using var host = AuthTestHost.Start();
        var id = await SeedUserAsync(host, "alice");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var mfa = scope.ServiceProvider.GetRequiredService<UserMfaService>();
            var alice = await users.FindByIdAsync(id.ToString());
            Assert.NotNull(alice);

            var enrolment = await mfa.BeginTotpAsync(alice, TestContext.Current.CancellationToken);
            Assert.NotNull(enrolment);
            var confirmed = await mfa.ConfirmTotpAsync(
                alice, TotpCodes.Current(enrolment.SharedKey), TestContext.Current.CancellationToken);
            Assert.Equal(UserMfaService.ConfirmOutcome.Enabled, confirmed.Outcome);
            Assert.NotNull(confirmed.Codes);
            Assert.Equal(UserMfaService.RecoveryCodeCount, confirmed.Codes.Count);

            Assert.True(await mfa.DisableAsync(alice, TestContext.Current.CancellationToken));
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var alice = await db.Users.SingleAsync(u => u.Id == id, TestContext.Current.CancellationToken);
            Assert.False(alice.TwoFactorEnabled);
            // All three, not just the flag: a key left behind would come back to life the moment two-factor
            // was switched on again.
            Assert.Null(alice.AuthenticatorKey);
            Assert.False(await db.UserRecoveryCodes.AnyAsync(
                c => c.UserId == id, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ConfirmTotp_RefusesAWrongCode_AndLeavesTwoFactorOff() {
        using var host = AuthTestHost.Start();
        var id = await SeedUserAsync(host, "alice");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var mfa = scope.ServiceProvider.GetRequiredService<UserMfaService>();
            var alice = await users.FindByIdAsync(id.ToString());
            Assert.NotNull(alice);

            var enrolment = await mfa.BeginTotpAsync(alice, TestContext.Current.CancellationToken);
            Assert.NotNull(enrolment);
            var refused = await mfa.ConfirmTotpAsync(
                alice, TotpCodes.Wrong(enrolment.SharedKey), TestContext.Current.CancellationToken);
            Assert.Equal(UserMfaService.ConfirmOutcome.RejectedCode, refused.Outcome);
            Assert.Null(refused.Codes);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var alice = await db.Users.SingleAsync(u => u.Id == id, TestContext.Current.CancellationToken);
            Assert.False(alice.TwoFactorEnabled);
            // An abandoned enrolment leaves a key nobody uses, never an account nobody can enter.
            Assert.NotNull(alice.AuthenticatorKey);
            Assert.False(await db.UserRecoveryCodes.AnyAsync(
                c => c.UserId == id, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void OtpauthUri_NamesWatchtowerAsBothLabelAndIssuer_AndEscapesTheAccount() {
        var uri = UserMfaService.BuildOtpauthUri("ops team", "ABCDEFGH");

        Assert.StartsWith("otpauth://totp/Watchtower:ops%20team?", uri, StringComparison.Ordinal);
        Assert.Contains("secret=ABCDEFGH", uri, StringComparison.Ordinal);
        // Apps that predate the parameter read the label prefix; the rest read this.
        Assert.Contains("issuer=Watchtower", uri, StringComparison.Ordinal);
    }

    // -- Helpers ---------------------------------------------------------------------------------

    private static async Task<int> SeedUserAsync(AuthTestHost host, string userName) {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = AuthTestHost.NewUser(userName);
        var created = await users.CreateAsync(user, GoodPassword);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user.Id;
    }

    private static async Task<IReadOnlyList<string>> GenerateCodesAsync(
        AuthTestHost host, int userId, int count) {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.FindByIdAsync(userId.ToString());
        Assert.NotNull(user);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, count);
        Assert.NotNull(codes);
        return codes.ToArray();
    }
}
