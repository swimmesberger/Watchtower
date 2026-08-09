using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Issues, validates and revokes the server-side login sessions behind the <c>__wt_sso</c> cookie
/// (docs/central-auth/design.md §4). Sessions are database rows, not self-contained cookie tickets:
/// deleting the row signs the session out immediately, which is what makes central logout — and the
/// per-app sessions of the forward-auth work — revocable at all.
/// </summary>
/// <remarks>
/// The cookie carries a 256-bit random token; only its SHA-256 hash reaches the database, so a database
/// read can neither reconstruct nor replay a live cookie. Lookups are by hash (a single indexed
/// point-read), so there is no secret-dependent comparison to time.
/// </remarks>
public sealed class AuthSessionService(
    WatchtowerDbContext db,
    IOptionsMonitor<WatchtowerOptions> options,
    TimeProvider time) {

    /// <summary>Name of the central single-sign-on cookie. Host-scoped: no <c>Domain</c> attribute is ever set.</summary>
    public const string SsoCookieName = "__wt_sso";

    /// <summary>Entropy of a session token, in bytes (256 bits).</summary>
    private const int TokenByteLength = 32;

    /// <summary>
    /// Idle lifetime a session is extended to. Clamped to a sane range so a mistyped configuration value
    /// cannot produce a session that expires instantly (or effectively never).
    /// </summary>
    public TimeSpan SlidingLifetime =>
        TimeSpan.FromHours(Math.Clamp(options.CurrentValue.Auth.SessionLifetimeHours, 1, 24 * 30));

    /// <summary>
    /// Hard cap on a session's age measured from <see cref="AuthSession.CreatedAt"/>. Never shorter than
    /// <see cref="SlidingLifetime"/> — an absolute cap below the idle window would make the sliding window
    /// meaningless rather than stricter.
    /// </summary>
    public TimeSpan AbsoluteLifetime {
        get {
            var absolute = TimeSpan.FromDays(Math.Clamp(options.CurrentValue.Auth.AbsoluteSessionLifetimeDays, 1, 365));
            return absolute < SlidingLifetime ? SlidingLifetime : absolute;
        }
    }

    /// <summary>
    /// Creates a central SSO session for <paramref name="user"/> and returns the raw cookie token — the only
    /// time it exists outside the caller's browser, since only its hash is stored.
    /// </summary>
    public async Task<string> CreateSsoSessionAsync(User user, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(user);

        var now = time.GetUtcNow();
        await SweepExpiredAsync(now, ct);

        var token = NewToken();
        db.AuthSessions.Add(new AuthSession {
            TokenHash = HashToken(token),
            UserId = user.Id,
            Kind = SessionKind.Sso,
            RouteId = null,
            CreatedAt = now,
            // SlidingLifetime <= AbsoluteLifetime by construction, so the initial expiry is already within the cap.
            ExpiresAt = now + SlidingLifetime,
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    /// <summary>
    /// Resolves a raw cookie token to its live session (with <see cref="AuthSession.User"/> loaded), or
    /// <see langword="null"/> when the token is unknown, expired, or belongs to a disabled account.
    /// </summary>
    /// <remarks>
    /// Renews the sliding window as a side effect, but only once less than half of it remains: a write on
    /// every request would turn each page load into a database write for no security benefit. The renewed
    /// expiry is clamped to <see cref="AuthSession.CreatedAt"/> + <see cref="AbsoluteLifetime"/>.
    /// <para>
    /// The absolute cap is re-derived here rather than trusted from the stored
    /// <see cref="AuthSession.ExpiresAt"/>, so <em>shortening</em> <c>AbsoluteSessionLifetimeDays</c> takes
    /// effect for sessions that were already issued under the longer setting — an operator tightening the
    /// policy after an incident should not have to wait out the old cap.
    /// </para>
    /// </remarks>
    public async Task<AuthSession?> ValidateAsync(string? rawToken, CancellationToken ct = default) {
        if (string.IsNullOrEmpty(rawToken)) return null;

        var hash = HashToken(rawToken);
        var session = await db.AuthSessions
            .Include(s => s.User)
            .FirstOrDefaultAsync(s => s.TokenHash == hash && s.Kind == SessionKind.Sso, ct);
        if (session is null) return null;

        var now = time.GetUtcNow();
        if (session.ExpiresAt <= now || session.CreatedAt + AbsoluteLifetime <= now) {
            // Expired by either clock: drop the row on the way past so a stale cookie stops costing a lookup.
            db.AuthSessions.Remove(session);
            await db.SaveChangesAsync(ct);
            return null;
        }

        // A disabled account keeps its rows (an administrator may re-enable it) but must not authenticate.
        if (session.User is null || session.User.Disabled) return null;

        var sliding = SlidingLifetime;
        if (session.ExpiresAt - now < sliding / 2) {
            var absoluteEnd = session.CreatedAt + AbsoluteLifetime;
            var renewed = now + sliding;
            if (renewed > absoluteEnd) renewed = absoluteEnd;
            if (renewed > session.ExpiresAt) {
                session.ExpiresAt = renewed;
                await db.SaveChangesAsync(ct);
            }
        }

        return session;
    }

    /// <summary>
    /// Deletes <em>every</em> session of the account — central and per-app alike. Logout is a global
    /// sign-out by design (design.md §4), so a compromised device cannot be left signed in to one app.
    /// </summary>
    /// <returns>How many sessions were revoked.</returns>
    public Task<int> RevokeAllForUserAsync(int userId, CancellationToken ct = default) =>
        db.AuthSessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(ct);

    /// <summary>
    /// Opportunistic lazy sweep of expired sessions of every kind. Logins are rare and already write, so this
    /// rides along instead of costing a background service (design.md §4) — it is housekeeping, not a control:
    /// <see cref="ValidateAsync"/> decides expiry from the row it just read, never from this having run.
    /// </summary>
    /// <remarks>
    /// Hand-written SQL because EF Core's SQLite provider cannot translate a <see cref="DateTimeOffset"/>
    /// comparison at all (SQLite has no date type; <c>Where(s =&gt; s.ExpiresAt &lt;= now)</c> throws at
    /// translation time, and so does <c>OrderBy</c>). The stored text is
    /// <c>yyyy-MM-dd HH:mm:ss.FFFFFFFzzz</c>, which sorts lexicographically as long as every value carries the
    /// same offset — hence the explicit <see cref="DateTimeOffset.ToUniversalTime"/>, matching the UTC instants
    /// this service writes. Table and column names mirror <c>AuthSessionConfiguration</c>; a drift there is
    /// caught by the sweep test rather than silently skipping the delete.
    /// </remarks>
    private async Task SweepExpiredAsync(DateTimeOffset now, CancellationToken ct) {
        var cutoff = now.ToUniversalTime();
        await db.Database.ExecuteSqlAsync($"DELETE FROM auth_sessions WHERE expires_at <= {cutoff}", ct);
    }

    /// <summary>The value stored in <see cref="AuthSession.TokenHash"/> for a raw cookie token.</summary>
    public static string HashToken(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    /// <summary>A fresh cookie token: 256 random bits in a URL/cookie-safe alphabet.</summary>
    private static string NewToken() =>
        Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenByteLength));
}
