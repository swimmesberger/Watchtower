using System.Text;
using Watchtower.Application.Services.Acme;

namespace Watchtower.Api.Proxy;

/// <summary>
/// Answers ACME HTTP-01 challenges on <c>/.well-known/acme-challenge/{token}</c> for <em>every</em> host
/// the process serves — ADR-0017 (forthcoming). First in the pipeline, ahead of the host dispatcher, so a
/// challenge is neither forwarded to an upstream nor redirected to HTTPS: the CA calls it over plain HTTP
/// on port 80, by definition, and both of those outcomes would fail the validation.
/// </summary>
/// <remarks>
/// Anonymous and host-agnostic, which is what the protocol requires — the CA arrives as an unauthenticated
/// stranger on the domain being validated, and Watchtower has no way to recognise it. What bounds the
/// exposure is that the only thing this can disclose is a key authorization the CA is about to be told
/// anyway, for a token Watchtower itself minted seconds earlier.
/// </remarks>
public sealed class AcmeChallengeMiddleware(RequestDelegate next, AcmeHttpChallengeStore store) {
    /// <summary>The well-known prefix from RFC 8555 §8.3; the one remaining segment is the token.</summary>
    private static readonly PathString ChallengePrefix = new("/.well-known/acme-challenge");

    public Task InvokeAsync(HttpContext context) {
        ArgumentNullException.ThrowIfNull(context);
        if (!context.Request.Path.StartsWithSegments(ChallengePrefix, out var remaining))
            return next(context);

        var token = Token(remaining);
        // Not a challenge URL at all (the bare prefix, or something nested under it). Falling through is
        // right here and only here: an upstream may legitimately serve its own /.well-known tree.
        if (token is null) return next(context);

        if (!store.TryGet(token, out var keyAuthorization)) {
            // Deliberately answered rather than passed on. On a route host the fall-through would forward
            // this to the upstream, which turns "is this domain proxied by Watchtower, and to what?" into a
            // question any stranger can ask by requesting a token that was never issued.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/plain";
        // The CA compares the body to the expected key authorization; RFC 8555 §8.3 allows trailing
        // whitespace to be ignored, but writing the value exactly is the only form nothing can quarrel with.
        context.Response.Headers.CacheControl = "no-store";
        var body = Encoding.UTF8.GetBytes(keyAuthorization);
        context.Response.ContentLength = body.Length;
        return context.Response.Body.WriteAsync(body, context.RequestAborted).AsTask();
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
