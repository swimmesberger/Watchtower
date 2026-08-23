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

    /// <summary>
    /// Name of the per-app session cookie, set by the callback on the app's own domain. Host-scoped, so
    /// each protected app gets its own — which is the whole reason the redirect dance exists (design.md §2.2).
    /// </summary>
    public const string AccessCookieName = "__wt_access";

    /// <summary>Entropy of a session token, in bytes (256 bits).</summary>
    private const int TokenByteLength = 32;

    /// <summary>
    /// How long a login code stays redeemable. It exists only to survive one redirect hop, so the window
    /// is the round trip and nothing more.
    /// </summary>
    public static readonly TimeSpan LoginCodeLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a pending-MFA record stays finishable. Long enough to unlock a phone and read a code off
    /// an authenticator app — or to find the printed recovery codes — and short enough that a token
    /// captured from a response body is worthless by the time anyone gets to it.
    /// </summary>
    public static readonly TimeSpan MfaPendingLifetime = TimeSpan.FromMinutes(5);

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
    public Task<string> CreateSsoSessionAsync(User user, CancellationToken ct = default) =>
        CreateSessionAsync(user, SessionKind.Sso, routeId: null, lifetime: null, ct);

    /// <summary>
    /// Creates a session for one protected app — the row behind its <c>__wt_access</c> cookie — and returns
    /// the raw cookie token. Minted only by the callback endpoint, from a redeemed login code.
    /// </summary>
    /// <remarks>
    /// The session is bound to <paramref name="routeId"/>: <see cref="ValidateAppSessionAsync"/> refuses it
    /// for any other route, so a cookie captured from one app cannot be replayed against another (they are
    /// different hosts, but the binding does not rely on the browser getting the scoping right).
    /// </remarks>
    public Task<string> CreateAppSessionAsync(User user, int routeId, CancellationToken ct = default) =>
        CreateSessionAsync(user, SessionKind.App, routeId, lifetime: null, ct);

    private async Task<string> CreateSessionAsync(
        User user, SessionKind kind, int? routeId, TimeSpan? lifetime, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(user);

        var now = time.GetUtcNow();
        await SweepExpiredAsync(now, ct);

        var token = NewToken();
        db.AuthSessions.Add(new AuthSession {
            TokenHash = HashToken(token),
            UserId = user.Id,
            Kind = kind,
            RouteId = routeId,
            CreatedAt = now,
            // SlidingLifetime <= AbsoluteLifetime by construction, so the initial expiry is already within the cap.
            ExpiresAt = now + (lifetime ?? SlidingLifetime),
        });
        await db.SaveChangesAsync(ct);
        return token;
    }

    // ── Pending MFA: the half of a login that the password alone does not finish ──────────

    /// <summary>
    /// Records that <paramref name="user"/> passed the password check but still owes a second factor, and
    /// returns the raw token the login page hands back to <c>POST /api/auth/login/mfa</c>.
    /// </summary>
    /// <remarks>
    /// Hashed at rest and swept on expiry like every other row here, but it is <em>not</em> a session: it
    /// is never set as a cookie, and <see cref="SessionKind.MfaPending"/> is excluded from every path that
    /// turns a token into an authenticated principal. What it carries is a single fact — which account the
    /// next request may finish signing in — for <see cref="MfaPendingLifetime"/>.
    /// </remarks>
    public Task<string> CreateMfaPendingAsync(User user, CancellationToken ct = default) =>
        CreateSessionAsync(user, SessionKind.MfaPending, routeId: null, MfaPendingLifetime, ct);

    /// <summary>
    /// Resolves a pending-MFA token to its record (with <see cref="AuthSession.User"/> and the user's realm
    /// loaded), or <see langword="null"/> when it is unknown, expired, or belongs to a disabled account.
    /// </summary>
    /// <remarks>
    /// Deliberately a peek, not a consume: a wrong code must not burn the challenge, or one mistyped digit
    /// would send the visitor back to the password form. The record survives until
    /// <see cref="ConsumeMfaPendingAsync"/> accepts it or it expires; brute force is bounded by the account
    /// lockout the failing endpoint drives and by the five-minute window, not by this lookup.
    /// </remarks>
    public async Task<AuthSession?> FindMfaPendingAsync(string? rawToken, CancellationToken ct = default) {
        if (string.IsNullOrEmpty(rawToken)) return null;

        var hash = HashToken(rawToken);
        var pending = await db.AuthSessions
            .Include(s => s.User)
            .ThenInclude(u => u!.Realm)
            .FirstOrDefaultAsync(s => s.TokenHash == hash && s.Kind == SessionKind.MfaPending, ct);
        if (pending is null) return null;

        if (pending.ExpiresAt <= time.GetUtcNow()) {
            db.AuthSessions.Remove(pending);
            await db.SaveChangesAsync(ct);
            return null;
        }

        return pending.User is null || pending.User.Disabled ? null : pending;
    }

    /// <summary>
    /// Spends the pending-MFA record, reporting whether this call is the one that got it.
    /// </summary>
    /// <remarks>
    /// The delete is the claim, exactly as in <see cref="RedeemLoginCodeAsync"/>: two requests that both
    /// present a correct code for the same challenge produce one session, not two.
    /// </remarks>
    public async Task<bool> ConsumeMfaPendingAsync(int pendingId, CancellationToken ct = default) {
        var claimed = await db.AuthSessions
            .Where(s => s.Id == pendingId && s.Kind == SessionKind.MfaPending)
            .ExecuteDeleteAsync(ct);
        return claimed == 1;
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
    public Task<AuthSession?> ValidateAsync(string? rawToken, CancellationToken ct = default) =>
        ValidateCoreAsync(rawToken, SessionKind.Sso, routeId: null, ct);

    /// <summary>
    /// Resolves an <c>__wt_access</c> cookie token to its live session, requiring that the session was
    /// minted for <paramref name="routeId"/>. Returns <see langword="null"/> for an unknown, expired,
    /// wrong-route or disabled-account token — the verify endpoint treats all of those identically.
    /// </summary>
    public Task<AuthSession?> ValidateAppSessionAsync(string? rawToken, int routeId, CancellationToken ct = default) =>
        ValidateCoreAsync(rawToken, SessionKind.App, routeId, ct);

    /// <summary>
    /// Resolves a raw cookie token to its live session (with <see cref="AuthSession.User"/> loaded) by hash
    /// alone — any kind, no route binding — or <see langword="null"/> when the token is unknown, expired, or
    /// belongs to a disabled account. The same-origin path the UserInfo endpoint uses for the
    /// <c>__wt_access</c> cookie, where identifying the account is the whole job and there is no route to
    /// bind against. Deliberately does <em>not</em> renew the sliding window: UserInfo is a read, not a page
    /// visit, and must not silently extend a session's life.
    /// <para>
    /// "Any kind" means any kind of <em>session</em>, and the filter names those two rather than excluding
    /// what is not one. An allow-list is the difference between a rule and a habit here: a deny-list would
    /// silently admit the next <see cref="SessionKind"/> anybody adds, and the kind most likely to be added
    /// is another half-authenticated state like <see cref="SessionKind.MfaPending"/>. Adding a kind must
    /// mean deciding, in this line, whether it is an identity.
    /// </para>
    /// </summary>
    public async Task<AuthSession?> ValidateAnyAsync(string? rawToken, CancellationToken ct = default) {
        if (string.IsNullOrEmpty(rawToken)) return null;

        var hash = HashToken(rawToken);
        var session = await db.AuthSessions
            .Include(s => s.User)
            .ThenInclude(u => u!.Realm)
            .FirstOrDefaultAsync(
                s => s.TokenHash == hash && (s.Kind == SessionKind.Sso || s.Kind == SessionKind.App), ct);
        if (session is null) return null;

        var now = time.GetUtcNow();
        if (session.ExpiresAt <= now || session.CreatedAt + AbsoluteLifetime <= now) {
            db.AuthSessions.Remove(session);
            await db.SaveChangesAsync(ct);
            return null;
        }

        // A disabled account keeps its rows but must not authenticate — freshness matters for UserInfo.
        return session.User is null || session.User.Disabled ? null : session;
    }

    private async Task<AuthSession?> ValidateCoreAsync(
        string? rawToken, SessionKind kind, int? routeId, CancellationToken ct) {
        if (string.IsNullOrEmpty(rawToken)) return null;

        var hash = HashToken(rawToken);
        // The realm rides along with the account: the principal minted from an SSO session carries the
        // realm claim, and the assertion minted from an app session carries the realm's issuer, so both
        // ends of the session lookup need it and neither should cost a second round trip for it. The
        // realm's login route comes too, because the issuer *is* its domain (ADR-0023) — without it
        // `RealmResolver.LoginHostForAsync` would go back to the database on every verified request
        // through the proxy, which is the hottest path there is here.
        var session = await db.AuthSessions
            .Include(s => s.User)
            .ThenInclude(u => u!.Realm)
            .ThenInclude(r => r!.LoginRoute)
            .FirstOrDefaultAsync(s => s.TokenHash == hash && s.Kind == kind, ct);
        if (session is null) return null;

        // Wrong app: refuse, but leave the row alone — it is a perfectly good session for its own route,
        // and deleting it here would let a request to app B sign the visitor out of app A.
        if (routeId is not null && session.RouteId != routeId) return null;

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
    /// Deletes just the app session behind <paramref name="rawToken"/> — the per-app sign-out
    /// (<c>/.watchtower/logout</c>), which must not touch the visitor's other apps or their central session.
    /// </summary>
    /// <returns><see langword="true"/> when a row was actually removed.</returns>
    public async Task<bool> RevokeAppSessionAsync(string? rawToken, CancellationToken ct = default) {
        if (string.IsNullOrEmpty(rawToken)) return false;
        var hash = HashToken(rawToken);
        var removed = await db.AuthSessions
            .Where(s => s.TokenHash == hash && s.Kind == SessionKind.App)
            .ExecuteDeleteAsync(ct);
        return removed > 0;
    }

    // ── Login codes: the one-time cross-domain handoff (design.md §5) ──────────

    /// <summary>What a redeemed login code authorises. The row itself is gone by the time this is returned.</summary>
    public sealed record LoginCodeGrant(int UserId, int RouteId, string RedirectUri);

    /// <summary>
    /// Mints a single-use code binding <paramref name="userId"/> to <paramref name="routeId"/> and the
    /// already-validated <paramref name="redirectUri"/>, and returns the raw value for the callback URL.
    /// Only its hash is stored, so the redirect URL in a proxy log is not a redeemable credential once
    /// the row is gone.
    /// </summary>
    public async Task<string> MintLoginCodeAsync(
        int userId, int routeId, string redirectUri, CancellationToken ct = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        var now = time.GetUtcNow();
        await SweepExpiredCodesAsync(now, ct);

        var code = NewToken();
        db.LoginCodes.Add(new LoginCode {
            CodeHash = HashToken(code),
            UserId = userId,
            RouteId = routeId,
            RedirectUri = redirectUri,
            CreatedAt = now,
            ExpiresAt = now + LoginCodeLifetime,
        });
        await db.SaveChangesAsync(ct);
        return code;
    }

    /// <summary>
    /// Consumes <paramref name="rawCode"/> and returns what it authorises, or <see langword="null"/> when
    /// it is unknown, already redeemed, or expired.
    /// </summary>
    /// <remarks>
    /// The delete is the claim, not a follow-up to it: the row is removed by a single statement whose
    /// affected-row count decides the outcome, so two concurrent redemptions of the same code produce one
    /// winner and one <see langword="null"/> rather than two app sessions. Expiry is checked after that —
    /// an expired code is consumed as well, since nothing is gained by leaving it redeemable-but-refused.
    /// </remarks>
    public async Task<LoginCodeGrant?> RedeemLoginCodeAsync(string? rawCode, CancellationToken ct = default) {
        if (string.IsNullOrEmpty(rawCode)) return null;

        var hash = HashToken(rawCode);
        var code = await db.LoginCodes.AsNoTracking().FirstOrDefaultAsync(c => c.CodeHash == hash, ct);
        if (code is null) return null;

        var claimed = await db.LoginCodes.Where(c => c.Id == code.Id).ExecuteDeleteAsync(ct);
        if (claimed != 1) return null;

        return code.ExpiresAt <= time.GetUtcNow()
            ? null
            : new LoginCodeGrant(code.UserId, code.RouteId, code.RedirectUri);
    }

    /// <summary>
    /// Opportunistic sweep of expired codes, riding along with the mint that is already writing. Same
    /// hand-written-SQL reason as <see cref="SweepExpiredAsync"/>.
    /// </summary>
    private async Task SweepExpiredCodesAsync(DateTimeOffset now, CancellationToken ct) {
        var cutoff = now.ToUniversalTime();
        await db.Database.ExecuteSqlAsync($"DELETE FROM login_codes WHERE expires_at <= {cutoff}", ct);
    }

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
