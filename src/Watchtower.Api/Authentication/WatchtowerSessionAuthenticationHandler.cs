using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;

namespace Watchtower.Api.Authentication;

/// <summary>Constants for Watchtower's native login scheme.</summary>
public static class WatchtowerSessionDefaults {
    /// <summary>The ASP.NET authentication scheme backed by the <c>auth_sessions</c> table.</summary>
    public const string AuthenticationScheme = "WatchtowerSession";
}

/// <summary>
/// Turns the <c>__wt_sso</c> cookie into an authenticated <see cref="ClaimsPrincipal"/> by looking the
/// session up in the database (docs/central-auth/design.md §2.5, §4).
/// </summary>
/// <remarks>
/// Deliberately not ASP.NET's cookie-authentication handler: its tickets are self-contained and stay valid
/// until they expire, so "sign this user out everywhere" — the contract central logout and the per-app
/// sessions of the forward-auth work both depend on — would be unimplementable. Here the cookie is an
/// opaque lookup key and the row is the authority, so deleting the row ends the session on the next request.
/// <para>
/// A missing, unknown, expired or disabled-account cookie yields <see cref="AuthenticateResult.NoResult"/>
/// rather than a failure: the caller is simply anonymous, and the endpoint's own requirements decide
/// whether that is a 401. The base challenge behaviour (plain 401, no redirect) is what an API and an SPA
/// both want — the login page is reached by the client router, never by a server-side redirect.
/// </para>
/// </remarks>
public sealed class WatchtowerSessionAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AuthSessionService sessions) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder) {

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync() {
        if (!Request.Cookies.TryGetValue(AuthSessionService.SsoCookieName, out var token) ||
            string.IsNullOrEmpty(token)) {
            return AuthenticateResult.NoResult();
        }

        // Deliberately not Context.RequestAborted: validation also writes (the sliding renewal, and the
        // delete of an expired row), and a client that hangs up mid-request would turn those into a
        // cancellation exception surfacing out of the authentication handler rather than a clean anonymous
        // result. The work is a single indexed point-read plus at most one small write; it does not need
        // to be interruptible.
        var session = await sessions.ValidateAsync(token, CancellationToken.None);
        if (session?.User is null) return AuthenticateResult.NoResult();

        var principal = new ClaimsPrincipal(CreateIdentity(session.User));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <summary>
    /// Projects the account onto the claims <c>ICurrentUser</c> reads. The identity is constructed with the
    /// same name/role claim types so <c>ClaimsPrincipal.IsInRole</c> and Elarion's snapshot agree.
    /// </summary>
    internal static ClaimsIdentity CreateIdentity(User user) {
        var claims = new List<Claim>(4) {
            new(WatchtowerClaims.UserId, user.Id.ToString(CultureInfo.InvariantCulture)),
            new(WatchtowerClaims.Name, user.UserName),
        };
        if (!string.IsNullOrWhiteSpace(user.Email))
            claims.Add(new Claim(WatchtowerClaims.Email, user.Email));
        if (user.IsAdmin)
            claims.Add(new Claim(WatchtowerClaims.Role, WatchtowerClaims.AdminRole));

        return new ClaimsIdentity(
            claims,
            WatchtowerSessionDefaults.AuthenticationScheme,
            WatchtowerClaims.Name,
            WatchtowerClaims.Role);
    }
}
