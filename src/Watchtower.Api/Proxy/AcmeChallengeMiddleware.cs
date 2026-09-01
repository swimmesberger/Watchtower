using System.Text;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Api.Proxy;

/// <summary>
/// Answers ACME HTTP-01 challenges on <c>/.well-known/acme-challenge/{token}</c> for <em>every</em> host
/// the process serves — ADR-0022. First in the pipeline, ahead of the host dispatcher, so a
/// challenge is neither forwarded to an upstream nor redirected to HTTPS: the CA calls it over plain HTTP
/// on port 80, by definition, and both of those outcomes would fail the validation.
/// </summary>
/// <remarks>
/// Anonymous and host-agnostic, which is what the protocol requires — the CA arrives as an unauthenticated
/// stranger on the domain being validated, and Watchtower has no way to recognise it. What bounds the
/// exposure is that the only thing this can disclose is a key authorization the CA is about to be told
/// anyway, for a token Watchtower itself minted seconds earlier.
/// <para>
/// The answer comes from the database since ADR-0024, which is what lets <em>any</em> instance satisfy a
/// validation the CA aimed at whichever node answers port 80. What that costs is bounded on purpose:
/// a request only reaches the store if its path is a challenge URL <em>and</em> its last segment is
/// shaped like a base64url token, and the store then answers a token this instance published from
/// memory and remembers a miss for a few seconds — so a stranger looping over invented tokens does not
/// turn into database load.
/// </para>
/// </remarks>
public sealed class AcmeChallengeMiddleware(
    RequestDelegate next,
    AcmeHttpChallengeStore store,
    YarpListenerState listener,
    ProxyIngressSection section) {
    /// <summary>The well-known prefix from RFC 8555 §8.3; the one remaining segment is the token.</summary>
    private static readonly PathString ChallengePrefix = new("/.well-known/acme-challenge");

    /// <summary>
    /// Whether <paramref name="localPort"/> carries a port route's own listener — the snapshot's answer
    /// or the projected section's, the same two readings the host dispatcher uses.
    /// </summary>
    private bool IsPortRouteListener(int localPort) =>
        listener.PortRoutePorts.Contains(localPort)
        || section.BoundPortRoutePorts().Contains(localPort);

    public async Task InvokeAsync(HttpContext context) {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Request.Path.StartsWithSegments(ChallengePrefix, out var remaining)) {
            await next(context);
            return;
        }

        // Not on a port route's own listener (ADR-0033). "Every host the process serves" is what makes
        // this middleware correct on the shared ingress ports, where a CA may arrive for any domain; a
        // port route's listener serves exactly one upstream, over TLS, on a LAN address no CA validates.
        // Answering here would hold a path an upstream is entitled to serve itself, and would do it for
        // a challenge that could never have been aimed at this address.
        //
        // Both readings, exactly as YarpHostDispatchMiddleware.IsPortRouteListener does it: the snapshot
        // lags the projection by a reload callback, so a listener that has just come up is a port route's
        // listener the section already knows about and the snapshot does not. One definition of the
        // question, or the two middlewares disagree about the same socket for the length of a reload.
        if (IsPortRouteListener(context.Connection.LocalPort)) {
            await next(context);
            return;
        }

        var token = Token(remaining);
        // Not a challenge URL at all (the bare prefix, or something nested under it). Falling through is
        // right here and only here: an upstream may legitimately serve its own /.well-known tree.
        if (token is null) {
            await next(context);
            return;
        }

        // Answered, not passed on, and without asking the store anything: this endpoint is anonymous and
        // reachable on port 80 for every host the proxy serves, so a string that is not shaped like a
        // token at all should cost a character scan and nothing more.
        if (!AcmeHttpChallengeStore.IsWellFormedToken(token)) {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var keyAuthorization = await store.TryGetAsync(token, context.RequestAborted);
        if (keyAuthorization is null) {
            // Deliberately answered rather than passed on. On a route host the fall-through would forward
            // this to the upstream, which turns "is this domain proxied by Watchtower, and to what?" into a
            // question any stranger can ask by requesting a token that was never issued.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/plain";
        // The CA compares the body to the expected key authorization; RFC 8555 §8.3 allows trailing
        // whitespace to be ignored, but writing the value exactly is the only form nothing can quarrel with.
        context.Response.Headers.CacheControl = "no-store";
        var body = Encoding.UTF8.GetBytes(keyAuthorization);
        context.Response.ContentLength = body.Length;
        await context.Response.Body.WriteAsync(body, context.RequestAborted);
    }

    /// <summary>
    /// The token in <paramref name="remaining"/> — exactly one non-empty segment — or
    /// <see langword="null"/> when the path is not a challenge URL.
    /// </summary>
    private static string? Token(PathString remaining) {
        var value = remaining.Value;
        if (string.IsNullOrEmpty(value) || value[0] != '/') return null;
        var token = value[1..];
        return token.Length == 0 || token.Contains('/', StringComparison.Ordinal) ? null : token;
    }
}

/// <summary>Pipeline registration for <see cref="AcmeChallengeMiddleware"/>.</summary>
public static class AcmeChallengeMiddlewareExtensions {
    /// <summary>
    /// Answers ACME HTTP-01 challenges. Register before <see cref="YarpHostDispatchMiddleware"/>: the
    /// ordering is the point, not a preference.
    /// </summary>
    public static IApplicationBuilder UseAcmeHttpChallenge(this IApplicationBuilder app) =>
        app.UseMiddleware<AcmeChallengeMiddleware>();
}
