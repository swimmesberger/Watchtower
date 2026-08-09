using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="AuthSessionService"/> — the revocable, database-backed login session behind the
/// <c>__wt_sso</c> cookie (docs/central-auth/design.md §4).
/// </summary>
public sealed class AuthSessionServiceTests {
    private const string Password = "correct-horse-battery";

    [Fact]
    public async Task CreatedSession_StoresOnlyTheHash_AndValidatesWithTheRawToken() {
        using var host = AuthTestHost.Start();
        var userId = await CreateUserAsync(host, "alice");

        string token;
        await using (var scope = host.Services.CreateAsyncScope()) {
            token = await CreateSessionAsync(scope.ServiceProvider, userId);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var stored = await db.AuthSessions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);

            Assert.Equal(SessionKind.Sso, stored.Kind);
            Assert.Null(stored.RouteId);
            Assert.Equal(userId, stored.UserId);
            // The cookie value itself must be unreconstructable from the row.
            Assert.NotEqual(token, stored.TokenHash);
            Assert.Equal(AuthSessionService.HashToken(token), stored.TokenHash);
            Assert.Equal(host.Time.Now, stored.CreatedAt);
            Assert.Equal(host.Time.Now.AddHours(12), stored.ExpiresAt);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            var session = await sessions.ValidateAsync(token, TestContext.Current.CancellationToken);

            Assert.NotNull(session);
            Assert.Equal(userId, session.UserId);
            // The account travels with the session — the authentication handler builds its claims from it.
            Assert.NotNull(session.User);
            Assert.Equal("alice", session.User.UserName);
        }
    }

    [Fact]
    public async Task UnknownOrEmptyToken_DoesNotValidate() {
        using var host = AuthTestHost.Start();
        var userId = await CreateUserAsync(host, "alice");

        string token;
        await using (var scope = host.Services.CreateAsyncScope()) {
            token = await CreateSessionAsync(scope.ServiceProvider, userId);
        }

        await using (var check = host.Services.CreateAsyncScope()) {
            var sessions = check.ServiceProvider.GetRequiredService<AuthSessionService>();
            Assert.Null(await sessions.ValidateAsync(null, TestContext.Current.CancellationToken));
            Assert.Null(await sessions.ValidateAsync(string.Empty, TestContext.Current.CancellationToken));
            Assert.Null(await sessions.ValidateAsync("not-a-token", TestContext.Current.CancellationToken));
            // A one-character difference must not resolve — the lookup is on the hash, not a prefix.
            Assert.Null(await sessions.ValidateAsync(token + "x", TestContext.Current.CancellationToken));
            // The stored hash is not itself a usable credential.
            Assert.Null(await sessions.ValidateAsync(
                AuthSessionService.HashToken(token), TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task ExpiredSession_StopsValidating_AndItsRowIsDropped() {
        using var host = AuthTestHost.Start();
        var userId = await CreateUserAsync(host, "alice");

        string token;
        await using (var scope = host.Services.CreateAsyncScope()) {
            token = await CreateSessionAsync(scope.ServiceProvider, userId);
        }

        host.Time.Advance(TimeSpan.FromHours(12));

        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            Assert.Null(await sessions.ValidateAsync(token, TestContext.Current.CancellationToken));
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.AuthSessions.AnyAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task SlidingRenewal_IsThrottledToTheSecondHalfOfTheWindow() {
        using var host = AuthTestHost.Start();
        var userId = await CreateUserAsync(host, "alice");
        var createdAt = host.Time.Now;

        string token;
        await using (var scope = host.Services.CreateAsyncScope()) {
            token = await CreateSessionAsync(scope.ServiceProvider, userId);
        }

        // Five hours in, seven of the twelve remain — more than half, so no write is worth making.
        host.Time.Advance(TimeSpan.FromHours(5));
        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            var session = await sessions.ValidateAsync(token, TestContext.Current.CancellationToken);
            Assert.NotNull(session);
            Assert.Equal(createdAt.AddHours(12), session.ExpiresAt);
        }

        // Two hours later only five remain, so the window slides forward from *now*.
        host.Time.Advance(TimeSpan.FromHours(2));
        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            var session = await sessions.ValidateAsync(token, TestContext.Current.CancellationToken);
            Assert.NotNull(session);
            Assert.Equal(createdAt.AddHours(19), session.ExpiresAt);
        }

        // …and the renewal was persisted, not just applied to the in-memory instance.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var stored = await db.AuthSessions.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(createdAt.AddHours(19), stored.ExpiresAt);
        }
    }

    [Fact]
    public async Task SlidingRenewal_NeverReachesPastTheAbsoluteCap() {
        // Twelve-hour idle window, one-day hard cap.
        using var host = AuthTestHost.Start(("Watchtower:Auth:AbsoluteSessionLifetimeDays", "1"));
        var userId = await CreateUserAsync(host, "alice");
        var createdAt = host.Time.Now;

        string token;
        await using (var scope = host.Services.CreateAsyncScope()) {
            token = await CreateSessionAsync(scope.ServiceProvider, userId);
        }

        // Renew once well inside the cap: now + 12h = createdAt + 19h.
        host.Time.Advance(TimeSpan.FromHours(7));
        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            var session = await sessions.ValidateAsync(token, TestContext.Current.CancellationToken);
            Assert.NotNull(session);
            Assert.Equal(createdAt.AddHours(19), session.ExpiresAt);
        }

        // Renew again where the naive answer (createdAt + 26h) would outlive the cap.
        host.Time.Advance(TimeSpan.FromHours(7));
        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            var session = await sessions.ValidateAsync(token, TestContext.Current.CancellationToken);
            Assert.NotNull(session);
            Assert.Equal(createdAt.AddDays(1), session.ExpiresAt);
        }

        // Past the cap the session is gone no matter how recently it was used.
        host.Time.Advance(TimeSpan.FromHours(11));
        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            Assert.Null(await sessions.ValidateAsync(token, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task DisabledAccount_StopsValidating_ButKeepsItsRows() {
        using var host = AuthTestHost.Start();
        var userId = await CreateUserAsync(host, "alice");

        string token;
        await using (var scope = host.Services.CreateAsyncScope()) {
            token = await CreateSessionAsync(scope.ServiceProvider, userId);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
            user.Disabled = true;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            Assert.Null(await sessions.ValidateAsync(token, TestContext.Current.CancellationToken));

            // Disabling is reversible, so the session rows are not collateral damage.
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.True(await db.AuthSessions.AnyAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Revoking_SignsTheAccountOutEverywhere_AndLeavesOtherAccountsAlone() {
        using var host = AuthTestHost.Start();
        var aliceId = await CreateUserAsync(host, "alice");
        var bobId = await CreateUserAsync(host, "bob");

        string aliceFirst, aliceSecond, bobToken;
        await using (var scope = host.Services.CreateAsyncScope()) {
            aliceFirst = await CreateSessionAsync(scope.ServiceProvider, aliceId);
            aliceSecond = await CreateSessionAsync(scope.ServiceProvider, aliceId);
            bobToken = await CreateSessionAsync(scope.ServiceProvider, bobId);

            // A per-app session of the same account — logout is global, so this must go too (design.md §4).
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            db.AuthSessions.Add(new AuthSession {
                TokenHash = AuthSessionService.HashToken("an-app-session"),
                UserId = aliceId,
                Kind = SessionKind.App,
                CreatedAt = host.Time.Now,
                ExpiresAt = host.Time.Now.AddHours(12),
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            Assert.Equal(3, await sessions.RevokeAllForUserAsync(aliceId, TestContext.Current.CancellationToken));
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();
            Assert.Null(await sessions.ValidateAsync(aliceFirst, TestContext.Current.CancellationToken));
            Assert.Null(await sessions.ValidateAsync(aliceSecond, TestContext.Current.CancellationToken));
            Assert.NotNull(await sessions.ValidateAsync(bobToken, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task CreatingASession_SweepsExpiredRows() {
        using var host = AuthTestHost.Start();
        var userId = await CreateUserAsync(host, "alice");

        await using (var scope = host.Services.CreateAsyncScope()) {
            await CreateSessionAsync(scope.ServiceProvider, userId);
        }

        host.Time.Advance(TimeSpan.FromHours(13));

        await using (var scope = host.Services.CreateAsyncScope()) {
            await CreateSessionAsync(scope.ServiceProvider, userId);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            // Only the fresh one is left: nothing sweeps expired sessions in the background yet.
            var remaining = await db.AuthSessions.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
            Assert.Single(remaining);
            Assert.Equal(host.Time.Now, remaining[0].CreatedAt);
        }
    }

    private static async Task<int> CreateUserAsync(AuthTestHost host, string userName) {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = AuthTestHost.NewUser(userName);
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(e => e.Description)));
        return user.Id;
    }

    private static async Task<string> CreateSessionAsync(IServiceProvider scope, int userId) {
        var db = scope.GetRequiredService<WatchtowerDbContext>();
        var sessions = scope.GetRequiredService<AuthSessionService>();
        var user = await db.Users.SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        return await sessions.CreateSsoSessionAsync(user, TestContext.Current.CancellationToken);
    }
}
