using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the pending-MFA record: a row that lives in the sessions table, is hashed and swept like a
/// session, and must never be usable <em>as</em> one.
/// </summary>
/// <remarks>
/// That last property is the whole point of the type existing, and it is the one a reader cannot check by
/// looking at the record: it holds because three separate lookup paths exclude the kind, and a fourth
/// added later would not. So it is asserted here against every one of them.
/// </remarks>
public sealed class MfaPendingSessionTests {
    private const string GoodPassword = "correct-horse-battery";

    [Fact]
    public async Task PendingRecord_IsStoredHashed_AndNotAsACookieSession() {
        using var host = AuthTestHost.Start();
        var user = await SeedUserAsync(host, "alice");

        string token;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            token = await sessions.CreateMfaPendingAsync(user, TestContext.Current.CancellationToken);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var row = await db.AuthSessions.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(SessionKind.MfaPending, row.Kind);
            // Only the hash is at rest, exactly as for a real session token.
            Assert.Equal(AuthSessionService.HashToken(token), row.TokenHash);
            Assert.DoesNotContain(token, row.TokenHash, StringComparison.Ordinal);
            // Five minutes, not the session lifetime.
            Assert.Equal(row.CreatedAt + AuthSessionService.MfaPendingLifetime, row.ExpiresAt);
        }
    }

    /// <summary>
    /// The invariant: a pending token is not a session on any path that turns a token into an identity —
    /// the SSO cookie (<c>ValidateAsync</c>, which is what the authentication handler calls), the per-app
    /// cookie (<c>ValidateAppSessionAsync</c>) and the kind-agnostic UserInfo path (<c>ValidateAnyAsync</c>).
    /// </summary>
    [Fact]
    public async Task PendingToken_IsRefusedByEverySessionLookup() {
        using var host = AuthTestHost.Start();
        var user = await SeedUserAsync(host, "alice");

        await using var scope = host.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
        var ct = TestContext.Current.CancellationToken;
        var token = await sessions.CreateMfaPendingAsync(user, ct);

        Assert.Null(await sessions.ValidateAsync(token, ct));
        Assert.Null(await sessions.ValidateAppSessionAsync(token, routeId: 1, ct));
        Assert.Null(await sessions.ValidateAnyAsync(token, ct));

        // …and the refusals did not consume it: it is still a perfectly good challenge.
        Assert.NotNull(await sessions.FindMfaPendingAsync(token, ct));
    }

    [Fact]
    public async Task PendingRecord_LapsesAfterFiveMinutes_AndIsSweptAway() {
        using var host = AuthTestHost.Start();
        var user = await SeedUserAsync(host, "alice");

        string token;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            token = await sessions.CreateMfaPendingAsync(user, TestContext.Current.CancellationToken);
        }

        // Still good a moment before the window closes…
        host.Time.Advance(AuthSessionService.MfaPendingLifetime - TimeSpan.FromSeconds(1));
        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            Assert.NotNull(await sessions.FindMfaPendingAsync(token, TestContext.Current.CancellationToken));
        }

        // …and worthless a moment after, with the row dropped on the way past.
        host.Time.Advance(TimeSpan.FromSeconds(2));
        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            Assert.Null(await sessions.FindMfaPendingAsync(token, TestContext.Current.CancellationToken));
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.AuthSessions.AnyAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Consuming_SucceedsExactlyOnce() {
        using var host = AuthTestHost.Start();
        var user = await SeedUserAsync(host, "alice");

        await using var scope = host.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
        var ct = TestContext.Current.CancellationToken;

        var token = await sessions.CreateMfaPendingAsync(user, ct);
        var pending = await sessions.FindMfaPendingAsync(token, ct);
        Assert.NotNull(pending);

        // The delete is the claim: two requests presenting a correct code for one challenge produce one
        // session, not two.
        Assert.True(await sessions.ConsumeMfaPendingAsync(pending.Id, ct));
        Assert.False(await sessions.ConsumeMfaPendingAsync(pending.Id, ct));
        Assert.Null(await sessions.FindMfaPendingAsync(token, ct));
    }

    [Fact]
    public async Task PendingRecord_OfADisabledAccount_IsNotFinishable() {
        using var host = AuthTestHost.Start();
        var user = await SeedUserAsync(host, "alice");

        string token;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            token = await sessions.CreateMfaPendingAsync(user, TestContext.Current.CancellationToken);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(
                s => s.SetProperty(u => u.Disabled, true), TestContext.Current.CancellationToken);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            // Suspended between the password and the code: the challenge stops being finishable at once.
            Assert.Null(await sessions.FindMfaPendingAsync(token, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task UnknownToken_IsSimplyNotAChallenge() {
        using var host = AuthTestHost.Start();
        await SeedUserAsync(host, "alice");

        await using var scope = host.Services.CreateAsyncScope();
        var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
        var ct = TestContext.Current.CancellationToken;

        Assert.Null(await sessions.FindMfaPendingAsync(null, ct));
        Assert.Null(await sessions.FindMfaPendingAsync(string.Empty, ct));
        Assert.Null(await sessions.FindMfaPendingAsync("not-a-token", ct));
    }

    private static async Task<User> SeedUserAsync(AuthTestHost host, string userName) {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = AuthTestHost.NewUser(userName);
        var created = await users.CreateAsync(user, GoodPassword);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user;
    }
}
