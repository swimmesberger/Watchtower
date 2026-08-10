using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The per-IP login backstop (docs/central-auth/design.md §9): once one client fills the window its
/// attempts are answered <c>429</c> before the handler runs, the throttle body is the same generic shape
/// for every account — so it cannot become an account-existence oracle — and a legitimate login within
/// the limit is untouched. Exercised through the real pipeline because the limiter is host wiring, and a
/// hand-rebuilt approximation could pass while the shipped middleware order was wrong.
/// </summary>
public sealed class LoginRateLimitTests {
    private static (string Key, string? Value)[] AuthOn(int perMinute) => [
        ("Watchtower:Auth:Enabled", "true"),
        ("Watchtower:Auth:LoginRateLimitPerMinute", perMinute.ToString(CultureInfo.InvariantCulture)),
    ];

    private static HttpRequestMessage Login(string userName, string password) =>
        new(HttpMethod.Post, "/api/auth/login") { Content = JsonContent.Create(new { userName, password }) };

    [Fact]
    public async Task RapidLogins_FromOneClient_AreThrottledOnceTheWindowFills() {
        using var factory = new WatchtowerApiFactory(AuthOn(perMinute: 3));
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        // Three attempts fit the window — each a normal 401 for the wrong password, all under the
        // 5-attempt account lockout — so nothing but the limiter can explain what the fourth gets.
        for (var attempt = 0; attempt < 3; attempt++) {
            var within = await client.SendAsync(Login("admin", "wrong-password"), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, within.StatusCode);
        }

        var throttled = await client.SendAsync(Login("admin", "wrong-password"), ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);

        // A generic { message } body that names no account.
        using var body = JsonDocument.Parse(await throttled.Content.ReadAsStringAsync(ct));
        Assert.True(body.RootElement.TryGetProperty("message", out var message));
        Assert.False(string.IsNullOrWhiteSpace(message.GetString()));
        Assert.DoesNotContain("admin", message.GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.False(throttled.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Throttle_LooksTheSame_ForAKnownAndAnUnknownAccount() {
        using var factory = new WatchtowerApiFactory(AuthOn(perMinute: 1));
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        // Spend the single permit, then two throttled requests: one naming the real account, one not.
        // The limiter fires on the IP before the body is read, so both must be byte-for-byte identical —
        // otherwise the 429 would leak whether "admin" exists.
        await client.SendAsync(Login("admin", "wrong-password"), ct);
        var known = await client.SendAsync(Login("admin", "wrong-password"), ct);
        var unknown = await client.SendAsync(Login("no-such-account", "wrong-password"), ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, known.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, unknown.StatusCode);
        Assert.Equal(
            await known.Content.ReadAsStringAsync(ct),
            await unknown.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task LegitimateLogin_WithinTheLimit_StillSucceeds() {
        using var factory = new WatchtowerApiFactory(AuthOn(perMinute: 3));
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        var ok = await client.SendAsync(Login("admin", WatchtowerApiFactory.AdminPassword), ct);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.True(ok.Headers.Contains("Set-Cookie"));
    }
}
