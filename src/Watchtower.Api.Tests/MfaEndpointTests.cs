using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Tests;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// Covers two-factor login and the self-service enrolment surface through the real pipeline. The
/// properties worth testing here are the ones only the shipped wiring has: that the password step stops
/// short of a cookie, that the pending token is refused everywhere a real session is accepted, that a
/// wrong code is counted by the same lockout the password is, and what ends up in the audit trail.
/// </summary>
public sealed class MfaEndpointTests {
    /// <summary>
    /// The rate limiter is raised out of the way: it partitions <c>/api/auth/login</c> and
    /// <c>/api/auth/login/mfa</c> together by client IP, and the lockout test deliberately spends seven
    /// attempts on them. Throttling is <see cref="LoginRateLimitTests"/>'s subject, not this file's.
    /// </summary>
    private static (string Key, string? Value)[] AuthOn() => [
        ("Watchtower:Auth:Enabled", "true"),
        ("Watchtower:Auth:LoginRateLimitPerMinute", "100"),
    ];

    // -- Login ------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_WithTwoFactorOn_AnswersAChallengeAndSetsNoCookie() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var estate = await EnrolAsync(factory, client);

        var response = await client.SendAsync(Login(), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Nothing was minted: the password alone is half an answer, and a cookie here would be the bug.
        Assert.False(response.Headers.Contains("Set-Cookie"));

        var body = await ReadJsonAsync(response);
        Assert.True(body.GetProperty("mfaRequired").GetBoolean());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("mfaToken").GetString()));
        // …and nothing that would let the caller skip the second step.
        Assert.False(body.TryGetProperty("userName", out _));

        GC.KeepAlive(estate);
    }

    /// <summary>
    /// The pending token is a challenge, not a credential: presented as the SSO cookie it authenticates
    /// nothing, which is the invariant the whole design rests on.
    /// </summary>
    [Fact]
    public async Task PendingToken_IsNotASession() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        await EnrolAsync(factory, client);

        var challenge = await ChallengeAsync(client);

        var rpc = await SendRpcAsync(client, "credentials.list", $"{AuthSessionService.SsoCookieName}={challenge.Token}");
        // -32005 is the framework's unauthenticated code — the same answer a garbage cookie gets.
        Assert.Contains("-32005", rpc, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorrectCode_MintsTheSameSessionASingleFactorLoginWould() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var estate = await EnrolAsync(factory, client);
        var challenge = await ChallengeAsync(client);

        var response = await client.SendAsync(
            MfaLogin(new { mfaToken = challenge.Token, code = TotpCodes.Current(estate.SharedKey) }),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.Equal("admin", body.GetProperty("userName").GetString());
        Assert.True(body.GetProperty("isAdmin").GetBoolean());

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains($"{AuthSessionService.SsoCookieName}=", cookie, StringComparison.Ordinal);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);

        // The session is real, not merely announced.
        var rpc = await SendRpcAsync(client, "credentials.list", cookie.Split(';')[0]);
        Assert.Contains("\"result\"", rpc, StringComparison.Ordinal);

        // One row for the login, and it names the two-factor path rather than claiming a plain login.ok.
        var kinds = await AuditKindsAsync(factory);
        Assert.Contains("login.mfa.ok", kinds);
        Assert.DoesNotContain("login.ok", kinds.Skip(kinds.IndexOf("mfa.totp.enabled")));

        // The challenge is spent — replaying it cannot produce a second session.
        var replay = await client.SendAsync(
            MfaLogin(new { mfaToken = challenge.Token, code = TotpCodes.Current(estate.SharedKey) }),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    /// <summary>
    /// A wrong code keeps the challenge alive (one mistyped digit must not send the visitor back to the
    /// password form) but is counted by the account lockout — otherwise the five-minute window would be a
    /// free brute-force gallery for someone who already has the password.
    /// </summary>
    [Fact]
    public async Task WrongCode_KeepsTheChallenge_ButCountsTowardsTheLockout() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var estate = await EnrolAsync(factory, client);
        var challenge = await ChallengeAsync(client);
        var ct = TestContext.Current.CancellationToken;

        var wrong = TotpCodes.Wrong(estate.SharedKey);
        for (var attempt = 0; attempt < 5; attempt++) {
            var failed = await client.SendAsync(
                MfaLogin(new { mfaToken = challenge.Token, code = wrong }), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
            Assert.False(failed.Headers.Contains("Set-Cookie"));
        }

        // Five failures is the configured threshold, and it is the *same* counter the password uses.
        Assert.NotNull(await ReadLockoutEndAsync(factory));

        // Now even the right code is refused, for as long as the lockout stands.
        var correct = await client.SendAsync(
            MfaLogin(new { mfaToken = challenge.Token, code = TotpCodes.Current(estate.SharedKey) }), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
        Assert.False(correct.Headers.Contains("Set-Cookie"));

        var kinds = await AuditKindsAsync(factory);
        Assert.Equal(6, kinds.Count(k => k == "login.mfa.failed"));
    }

    [Fact]
    public async Task UnknownChallenge_IsRefusedIndistinguishablyFromAWrongCode() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var estate = await EnrolAsync(factory, client);
        var challenge = await ChallengeAsync(client);
        var ct = TestContext.Current.CancellationToken;

        var unknown = await client.SendAsync(
            MfaLogin(new { mfaToken = "not-a-real-challenge", code = "123456" }), ct);
        var wrongCode = await client.SendAsync(
            MfaLogin(new { mfaToken = challenge.Token, code = TotpCodes.Wrong(estate.SharedKey) }), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(wrongCode.StatusCode, unknown.StatusCode);
        // Saying which one it was would tell a caller holding a stolen password whether the challenge is
        // still alive and worth grinding.
        Assert.Equal(
            await wrongCode.Content.ReadAsStringAsync(ct),
            await unknown.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task RecoveryCode_SignsIn_AndIsSpentOnce() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var estate = await EnrolAsync(factory, client);
        var ct = TestContext.Current.CancellationToken;

        var challenge = await ChallengeAsync(client);
        var response = await client.SendAsync(
            MfaLogin(new { mfaToken = challenge.Token, recoveryCode = estate.RecoveryCodes[0] }), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            $"{AuthSessionService.SsoCookieName}=",
            Assert.Single(response.Headers.GetValues("Set-Cookie")),
            StringComparison.Ordinal);

        var kinds = await AuditKindsAsync(factory);
        Assert.Contains("login.mfa.ok", kinds);
        Assert.Contains("mfa.recovery.redeemed", kinds);

        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            // Nine left of ten, and the row for the spent one is gone rather than flagged.
            Assert.Equal(9, await db.UserRecoveryCodes.CountAsync(ct));
            var redeemed = await db.AuthEvents.SingleAsync(e => e.Kind == "mfa.recovery.redeemed", ct);
            Assert.Contains("remaining=9", redeemed.Detail);
        });

        // The same code a second time is worth nothing.
        var second = await ChallengeAsync(client);
        var replay = await client.SendAsync(
            MfaLogin(new { mfaToken = second.Token, recoveryCode = estate.RecoveryCodes[0] }), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task MfaLogin_RequiresJsonContentType() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        await EnrolAsync(factory, client);
        var challenge = await ChallengeAsync(client);

        // A cross-site HTML form can only produce these content types; refusing them is what keeps a forged
        // POST from completing somebody else's challenge.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login/mfa") {
            Content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("mfaToken", challenge.Token),
                new KeyValuePair<string, string>("code", "123456"),
            ]),
        };

        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    // -- Self-service ------------------------------------------------------------------------------

    [Fact]
    public async Task SelfService_RequiresASession() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/auth/mfa"), ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.SendAsync(Post("/api/auth/mfa/totp/begin", new { }, cookie: null), ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.SendAsync(Post("/api/auth/mfa/totp/confirm", new { code = "123456" }, null), ct)).StatusCode);
    }

    [Fact]
    public async Task Enrolment_TurnsTwoFactorOn_ShowsTheCodesOnce_AndAudits() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var estate = await EnrolAsync(factory, client);
        var ct = TestContext.Current.CancellationToken;

        // The shared key is a URI an authenticator app can consume, and the account it names is the caller's.
        Assert.Contains("otpauth://totp/Watchtower:admin?", estate.OtpauthUri, StringComparison.Ordinal);
        Assert.Contains($"secret={estate.SharedKey}", estate.OtpauthUri, StringComparison.Ordinal);
        Assert.Equal(UserMfaService.RecoveryCodeCount, estate.RecoveryCodes.Count);

        var status = await ReadJsonAsync(await client.SendAsync(Get("/api/auth/mfa", estate.Cookie), ct));
        Assert.True(status.GetProperty("totpEnabled").GetBoolean());
        Assert.Equal(
            UserMfaService.RecoveryCodeCount, status.GetProperty("recoveryCodesRemaining").GetInt32());

        var kinds = await AuditKindsAsync(factory);
        Assert.Contains("mfa.totp.enabled", kinds);
        Assert.Contains("mfa.recovery.generated", kinds);

        // Nothing anywhere in the status surface leaks the key or a code.
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var stored = await db.UserRecoveryCodes.Select(c => c.CodeHash).ToListAsync(ct);
            foreach (var code in estate.RecoveryCodes) Assert.DoesNotContain(code, stored);
        });
    }

    /// <summary>
    /// Re-enrolling while two-factor is on is refused, and that is a safety rule: a new key would silently
    /// invalidate the authenticator the owner is actually using and lock them out of their own account.
    /// </summary>
    [Fact]
    public async Task Begin_IsRefusedWhileTwoFactorIsAlreadyOn() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var estate = await EnrolAsync(factory, client);

        var response = await client.SendAsync(
            Post("/api/auth/mfa/totp/begin", new { }, estate.Cookie), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Confirm_WithAWrongCode_LeavesTwoFactorOff() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        var cookie = await SignInAsync(client);
        var begun = await ReadJsonAsync(
            await client.SendAsync(Post("/api/auth/mfa/totp/begin", new { }, cookie), ct));
        var sharedKey = begun.GetProperty("sharedKey").GetString()!;

        var response = await client.SendAsync(
            Post("/api/auth/mfa/totp/confirm", new { code = TotpCodes.Wrong(sharedKey) }, cookie), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var status = await ReadJsonAsync(await client.SendAsync(Get("/api/auth/mfa", cookie), ct));
        Assert.False(status.GetProperty("totpEnabled").GetBoolean());
        Assert.Equal(0, status.GetProperty("recoveryCodesRemaining").GetInt32());
        Assert.DoesNotContain("mfa.totp.enabled", await AuditKindsAsync(factory));
    }

    [Fact]
    public async Task Disable_ClearsEverything_AndTheNextLoginNeedsNoSecondFactor() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var estate = await EnrolAsync(factory, client);
        var ct = TestContext.Current.CancellationToken;

        var refused = await client.SendAsync(
            Post("/api/auth/mfa/totp/disable", new { code = TotpCodes.Wrong(estate.SharedKey) }, estate.Cookie), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        var disabled = await client.SendAsync(
            Post("/api/auth/mfa/totp/disable", new { code = TotpCodes.Current(estate.SharedKey) }, estate.Cookie), ct);
        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);

        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var admin = await db.Users.SingleAsync(u => u.UserName == "admin", ct);
            Assert.False(admin.TwoFactorEnabled);
            Assert.Null(admin.AuthenticatorKey);
            Assert.False(await db.UserRecoveryCodes.AnyAsync(ct));
        });

        Assert.Contains("mfa.totp.disabled", await AuditKindsAsync(factory));

        // And the password alone is a whole answer again.
        var login = await ReadJsonAsync(await client.SendAsync(Login(), ct));
        Assert.Equal("admin", login.GetProperty("userName").GetString());
    }

    [Fact]
    public async Task Disable_AcceptsARecoveryCode_ForTheOwnerWhoseAuthenticatorIsGone() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var estate = await EnrolAsync(factory, client);
        var ct = TestContext.Current.CancellationToken;

        var response = await client.SendAsync(
            Post("/api/auth/mfa/totp/disable", new { code = estate.RecoveryCodes[0] }, estate.Cookie), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.Users.AnyAsync(u => u.TwoFactorEnabled, ct));
            // The remaining nine go with it: codes on an account with no second factor are credentials
            // nothing checks the state of.
            Assert.False(await db.UserRecoveryCodes.AnyAsync(ct));
            var row = await db.AuthEvents.SingleAsync(e => e.Kind == "mfa.totp.disabled", ct);
            Assert.Contains("authorised by recovery code", row.Detail);
        });
    }

    [Fact]
    public async Task Regenerate_NeedsAnAuthenticatorCode_AndReplacesTheWholeSet() {
        using var factory = new WatchtowerApiFactory(AuthOn());
        using var client = factory.CreateApiClient();
        var estate = await EnrolAsync(factory, client);
        var ct = TestContext.Current.CancellationToken;

        // A recovery code is deliberately NOT enough: spending one to mint ten fresh ones would turn a
        // single leaked code into permanent access.
        var refused = await client.SendAsync(
            Post("/api/auth/mfa/recovery/regenerate", new { code = estate.RecoveryCodes[0] }, estate.Cookie), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        var response = await client.SendAsync(
            Post("/api/auth/mfa/recovery/regenerate", new { code = TotpCodes.Current(estate.SharedKey) }, estate.Cookie),
            ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var fresh = (await ReadJsonAsync(response)).GetProperty("recoveryCodes")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(UserMfaService.RecoveryCodeCount, fresh.Count);
        Assert.Empty(fresh.Intersect(estate.RecoveryCodes, StringComparer.Ordinal));

        // The old set stops working at the login endpoint, which is the point of regenerating.
        var challenge = await ChallengeAsync(client);
        var stale = await client.SendAsync(
            MfaLogin(new { mfaToken = challenge.Token, recoveryCode = estate.RecoveryCodes[1] }), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);

        Assert.Equal(2, (await AuditKindsAsync(factory)).Count(k => k == "mfa.recovery.generated"));
    }

    [Fact]
    public async Task Routes_Are404_WhenAuthorizationIsSwitchedOff() {
        using var factory = new WatchtowerApiFactory();
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.SendAsync(Get("/api/auth/mfa", cookie: null), ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.SendAsync(Post("/api/auth/login/mfa", new { }, null), ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.SendAsync(Post("/api/auth/mfa/totp/begin", new { }, null), ct)).StatusCode);
    }

    // -- Helpers -----------------------------------------------------------------------------------

    /// <summary>What a fully enrolled <c>admin</c> holds: a session, the shared key, and ten codes.</summary>
    private sealed record MfaEstate(
        string Cookie, string SharedKey, string OtpauthUri, IReadOnlyList<string> RecoveryCodes);

    /// <summary>A pending challenge, as the password step handed it back.</summary>
    private sealed record Challenge(string Token);

    private static HttpRequestMessage Login() =>
        new(HttpMethod.Post, "/api/auth/login") {
            Content = JsonContent.Create(new {
                userName = "admin", password = WatchtowerApiFactory.AdminPassword,
            }),
        };

    private static HttpRequestMessage MfaLogin(object body) =>
        new(HttpMethod.Post, "/api/auth/login/mfa") { Content = JsonContent.Create(body) };

    private static HttpRequestMessage Get(string path, string? cookie) {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return request;
    }

    private static HttpRequestMessage Post(string path, object body, string? cookie) {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        return request;
    }

    /// <summary>Signs in with the password alone and returns the <c>__wt_sso</c> cookie pair.</summary>
    private static async Task<string> SignInAsync(HttpClient client) {
        var response = await client.SendAsync(Login(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
    }

    /// <summary>
    /// Drives the whole self-service enrolment through the shipped endpoints — password login, begin,
    /// confirm — so every test that needs an enrolled account also exercises the way one is really made.
    /// </summary>
    private static async Task<MfaEstate> EnrolAsync(WatchtowerApiFactory factory, HttpClient client) {
        var ct = TestContext.Current.CancellationToken;
        var cookie = await SignInAsync(client);

        var begun = await ReadJsonAsync(
            await client.SendAsync(Post("/api/auth/mfa/totp/begin", new { }, cookie), ct));
        var sharedKey = begun.GetProperty("sharedKey").GetString()!;
        var otpauthUri = begun.GetProperty("otpauthUri").GetString()!;

        var confirmed = await client.SendAsync(
            Post("/api/auth/mfa/totp/confirm", new { code = TotpCodes.Current(sharedKey) }, cookie), ct);
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);

        var codes = (await ReadJsonAsync(confirmed)).GetProperty("recoveryCodes")
            .EnumerateArray().Select(e => e.GetString()!).ToList();

        return new MfaEstate(cookie, sharedKey, otpauthUri, codes);
    }

    /// <summary>Posts the password and returns the pending-MFA token it answers with.</summary>
    private static async Task<Challenge> ChallengeAsync(HttpClient client) {
        var response = await client.SendAsync(Login(), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJsonAsync(response);
        Assert.True(body.GetProperty("mfaRequired").GetBoolean());
        return new Challenge(body.GetProperty("mfaToken").GetString()!);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .RootElement.Clone();

    private static async Task<string> SendRpcAsync(HttpClient client, string method, string? cookie) {
        var request = new HttpRequestMessage(HttpMethod.Post, "/rpc") {
            Content = JsonContent.Create(new { jsonrpc = "2.0", method, @params = new { }, id = "1" }),
        };
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<List<string>> AuditKindsAsync(WatchtowerApiFactory factory) {
        List<string> kinds = [];
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            kinds = await db.AuthEvents.OrderBy(e => e.Id)
                .Select(e => e.Kind)
                .ToListAsync(TestContext.Current.CancellationToken);
        });
        return kinds;
    }

    private static async Task<DateTimeOffset?> ReadLockoutEndAsync(WatchtowerApiFactory factory) {
        DateTimeOffset? lockoutEnd = null;
        await factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            lockoutEnd = await db.Users
                .Where(u => u.UserName == AuthBootstrapService.AdminUserName)
                .Select(u => u.LockoutEnd)
                .SingleAsync(TestContext.Current.CancellationToken);
        });
        return lockoutEnd;
    }
}
