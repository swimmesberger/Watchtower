using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the hand-written <see cref="Services.WatchtowerUserStore"/> through the public surface the
/// login endpoint will use — <see cref="UserManager{TUser}"/> — rather than the store's own methods.
/// </summary>
public sealed class WatchtowerUserStoreTests {
    private const string GoodPassword = "correct-horse-battery";

    [Fact]
    public async Task Password_RoundTrips_AndAWrongOneCountsAsAFailure() {
        using var host = AuthTestHost.Start();

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var created = await users.CreateAsync(AuthTestHost.NewUser("alice"), GoodPassword);
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

            // Lookups go through the normalized column, so the login name is case-insensitive.
            var alice = await users.FindByNameAsync("AlIcE");
            Assert.NotNull(alice);
            Assert.NotEqual(0, alice.Id);
            Assert.NotEqual(string.Empty, alice.PasswordHash);
            Assert.NotEqual(GoodPassword, alice.PasswordHash);

            Assert.True(await users.CheckPasswordAsync(alice, GoodPassword));
            Assert.False(await users.CheckPasswordAsync(alice, "not-the-password"));
            await users.AccessFailedAsync(alice);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var alice = await users.FindByNameAsync("alice");
            Assert.NotNull(alice);
            // The counter was written through, not just held in memory.
            Assert.Equal(1, await users.GetAccessFailedCountAsync(alice));
            Assert.False(await users.IsLockedOutAsync(alice));
        }
    }

    [Fact]
    public async Task FiveFailedAttempts_LockTheAccount() {
        using var host = AuthTestHost.Start();

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.CreateAsync(AuthTestHost.NewUser("bob"), GoodPassword)).Succeeded);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var bob = await users.FindByNameAsync("bob");
            Assert.NotNull(bob);

            for (var attempt = 1; attempt <= 5; attempt++) {
                Assert.False(await users.IsLockedOutAsync(bob));
                Assert.False(await users.CheckPasswordAsync(bob, "not-the-password"));
                Assert.True((await users.AccessFailedAsync(bob)).Succeeded);
            }

            Assert.True(await users.IsLockedOutAsync(bob));
            var lockoutEnd = await users.GetLockoutEndDateAsync(bob);
            Assert.NotNull(lockoutEnd);
            // Configured policy: 15 minutes.
            Assert.InRange(lockoutEnd.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var bob = await users.FindByNameAsync("bob");
            Assert.NotNull(bob);
            // The lockout was persisted, not merely held on the tracked instance.
            Assert.True(await users.IsLockedOutAsync(bob));
        }
    }

    [Fact]
    public async Task ConcurrentUpdates_RejectTheSecondWriterInsteadOfLosingTheFirst() {
        using var host = AuthTestHost.Start();

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True((await users.CreateAsync(AuthTestHost.NewUser("dave"), GoodPassword)).Succeeded);
        }

        // Two administrators with the account open at the same time.
        await using var first = host.Services.CreateAsyncScope();
        await using var second = host.Services.CreateAsyncScope();
        var firstManager = first.ServiceProvider.GetRequiredService<UserManager<User>>();
        var secondManager = second.ServiceProvider.GetRequiredService<UserManager<User>>();

        var asSeenByFirst = await firstManager.FindByNameAsync("dave");
        var asSeenBySecond = await secondManager.FindByNameAsync("dave");
        Assert.NotNull(asSeenByFirst);
        Assert.NotNull(asSeenBySecond);

        Assert.True((await firstManager.UpdateAsync(asSeenByFirst)).Succeeded);

        var stale = await secondManager.UpdateAsync(asSeenBySecond);
        Assert.False(stale.Succeeded);
        Assert.Contains(stale.Errors, e => e.Code == nameof(IdentityErrorDescriber.ConcurrencyFailure));
    }

    [Fact]
    public async Task PasswordPolicy_RequiresLengthButNotSymbolClasses() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

        var tooShort = await users.CreateAsync(AuthTestHost.NewUser("carol"), "short1");
        Assert.False(tooShort.Succeeded);

        // All-lowercase, no digits or symbols: long enough is enough.
        var longEnough = await users.CreateAsync(AuthTestHost.NewUser("carol"), "allloweralpha");
        Assert.True(longEnough.Succeeded, string.Join("; ", longEnough.Errors.Select(e => e.Description)));
    }
}
