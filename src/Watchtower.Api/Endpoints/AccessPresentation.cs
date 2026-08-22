using System.Text.Encodings.Web;

namespace Watchtower.Api.Endpoints;

/// <summary>
/// How an <see cref="Application.Services.AccessDecision"/> is presented to a visitor, and how a request's
/// shape is read off it — the parts of the forward-auth surface that both transports need to agree on.
/// </summary>
/// <remarks>
/// There are two of those transports (ADR-0017, forthcoming): Caddy's <c>forward_auth</c> hop to
/// <c>GET /api/access/verify</c>, and the in-process host dispatcher, which asks
/// <see cref="Application.Services.AccessVerifier"/> directly and acts on the verdict itself. The
/// <em>decision</em> is already shared — that is what the verifier is for — and this is the other half:
/// a visitor refused by one must see the same page as a visitor refused by the other, and a request judged
/// a browser navigation by one must be judged one by the other. Two renderings or two readings of
/// "is this a navigation?" would be a difference nobody would notice until it mattered.
/// <para>
/// Everything here is a pure function of the request or of plain text. Nothing touches the database, and
/// nothing writes a response — the caller decides whether the markup becomes an <c>IResult</c> or bytes on
/// a <see cref="HttpResponse"/>.
/// </para>
/// </remarks>
internal static class AccessPresentation {
    /// <summary>
    /// Whether this is a request the visitor would follow with their eyes: a document fetch. It decides
    /// between a login redirect and a bare 401, so both transports have to read it the same way.
    /// </summary>
    /// <param name="trustForwardedMethod">
    /// Whether <c>X-Forwarded-Method</c> may name the method. True at the verify endpoint, where
    /// <c>forward_auth</c> sends the check itself as a GET and puts the original method in that header —
    /// there the hop is Caddy's and the header is the only place the real method survives. <b>False in
    /// process</b>, where there is no such hop: the header is then a string the <em>client</em> wrote, and
    /// honouring it would let a POST present itself as a navigation and collect a login redirect instead of
    /// the 401 a non-navigation is owed. The rest of the reading — GET or HEAD, and an <c>Accept</c> that
    /// admits HTML — is identical, which is why one helper still serves both.
    /// </param>
    public static bool IsBrowserNavigation(HttpContext http, bool trustForwardedMethod = true) {
        ArgumentNullException.ThrowIfNull(http);
        var method = http.Request.Method;
        if (trustForwardedMethod) {
            var forwarded = http.Request.Headers["X-Forwarded-Method"].ToString();
            if (!string.IsNullOrEmpty(forwarded)) method = forwarded;
        }
        if (!HttpMethods.IsGet(method) && !HttpMethods.IsHead(method)) return false;
        return http.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Audit detail: the remote address, never a cookie or a code.</summary>
    public static string Describe(HttpContext http) {
        ArgumentNullException.ThrowIfNull(http);
        return $"from {http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
    }

    /// <summary>
    /// A minimal self-contained page: no stylesheet, no script, and nothing taken from the request.
    /// </summary>
    /// <param name="messageHtml">
    /// The one interpolated fragment, and therefore the caller's responsibility: any value that is not a
    /// literal must already have been through <see cref="Encode"/>. <paramref name="title"/> and
    /// <paramref name="hint"/> are encoded here.
    /// </param>
    public static string Html(string title, string messageHtml, string hint) {
        var encodedTitle = Encode(title);
        return $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{encodedTitle}</title>
            </head>
            <body style="font:16px/1.5 system-ui,sans-serif;margin:0;display:grid;place-items:center;min-height:100vh">
            <main style="max-width:32rem;padding:2rem;text-align:center">
            <h1 style="font-size:1.25rem;margin:0 0 .5rem">{encodedTitle}</h1>
            <p style="margin:0 0 .5rem">{messageHtml}</p>
            <p style="margin:0;opacity:.7">{Encode(hint)}</p>
            </main>
            </body>
            </html>
            """;
    }

    /// <summary>HTML-encodes one plain-text value for interpolation into <see cref="Html"/>.</summary>
    public static string Encode(string value) => HtmlEncoder.Default.Encode(value);
}
