using Watchtower.Application.Config;

namespace Watchtower.Api.Authentication;

/// <summary>
/// The one place the attributes of Watchtower's session cookies are decided — the central
/// <c>__wt_sso</c> and every per-app <c>__wt_access</c> alike (docs/central-auth/design.md §4, §9).
/// </summary>
/// <remarks>
/// Shared rather than repeated per endpoint because the attributes are a security property, and because
/// a delete only works when it repeats the attributes the cookie was set with — two copies of this that
/// drifted apart would leave cookies that cannot be cleared.
/// </remarks>
internal static class AuthCookies {
    /// <summary>
    /// Host-scoped (no <c>Domain</c>), <c>HttpOnly</c>, <c>SameSite=Lax</c>, and <c>Secure</c> per
    /// <paramref name="securePolicy"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="AuthCookieSecurePolicy.Auto"/> reads <see cref="HttpRequest.IsHttps"/> <em>after</em>
    /// <c>UseForwardedHeaders</c> has applied <c>X-Forwarded-Proto</c>, so a request that reached the proxy
    /// over HTTPS is treated as secure even though the hop into Kestrel is plain HTTP. Setting the flag
    /// unconditionally instead would break the published-port recovery path, where the cookie would be set
    /// and then never sent back.
    /// <para>
    /// <c>SameSite=Lax</c> is load-bearing for the per-app cookie specifically: the callback that sets it is
    /// reached by a top-level redirect from the auth host, which Lax allows, while the cross-site POSTs that
    /// CSRF needs it does not.
    /// </para>
    /// </remarks>
    public static void Append(
        HttpContext http, string name, string token, TimeSpan maxAge, AuthCookieSecurePolicy securePolicy) =>
        http.Response.Cookies.Append(name, token, new CookieOptions {
            HttpOnly = true,
            Secure = IsSecure(http, securePolicy),
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            // The database row is the authority on expiry; MaxAge only stops the browser from keeping a
            // value that can no longer be valid, so it tracks the absolute cap rather than the idle window.
            MaxAge = maxAge,
        });

    /// <summary>Expires the cookie. The attributes must match the ones it was set with or the browser keeps it.</summary>
    public static void Delete(HttpContext http, string name, AuthCookieSecurePolicy securePolicy) =>
        http.Response.Cookies.Delete(name, new CookieOptions {
            HttpOnly = true,
            Secure = IsSecure(http, securePolicy),
            SameSite = SameSiteMode.Lax,
            Path = "/",
        });

    /// <summary>Resolves the configured policy against this request.</summary>
    private static bool IsSecure(HttpContext http, AuthCookieSecurePolicy policy) => policy switch {
        AuthCookieSecurePolicy.Always => true,
        AuthCookieSecurePolicy.Never => false,
        _ => http.Request.IsHttps,
    };
}
