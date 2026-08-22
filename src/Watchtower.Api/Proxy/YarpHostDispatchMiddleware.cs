using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Watchtower.Api.Endpoints;
using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;
using Yarp.ReverseProxy.Forwarder;

namespace Watchtower.Api.Proxy;

/// <summary>
/// The in-process proxy's request path — ADR-0017. Every request is dispatched by its
/// <c>Host</c> <em>before</em> Watchtower's own pipeline: a host in the route table is access-checked and
/// forwarded to its container, and everything else falls through to the app that was always there.
/// </summary>
/// <remarks>
/// This is the in-process rendering of the site block <see cref="CaddyConfigBuilder"/> writes for the same
/// route (design.md §6), and for the same reasons — the identity strip, then <c>/.watchtower/*</c> served
/// locally because the callback has to answer while the visitor is still anonymous, then the access
/// decision, then the upstream. The strip runs first here rather than inside the forwarded branch, which is
/// a deliberate strengthening: Caddy's generated config strips only on the path that reaches the upstream,
/// and there is no reason for a smuggled <c>Remote-User</c> to survive as far as our own endpoints either.
/// What differs otherwise is only the mechanism: the decision comes from
/// <see cref="AccessVerifier"/> directly instead of an HTTP hop to <c>/api/access/verify</c>, so the two
/// providers cannot come to different verdicts about who may enter an app.
/// <para>
/// <b>Where it sits.</b> Registered immediately after the database initialisation and <em>before</em>
/// <c>UseForwardedHeaders</c>. Before, because this middleware's scheme decisions have to come from the
/// real connection: reading them from an inbound <c>X-Forwarded-Proto</c> would let a client turn the
/// HTTP→HTTPS redirect off for itself, or on for a plain-HTTP deployment, which is a redirect loop.
/// <c>WebApplication</c> inserts <c>UseRouting</c> at the very front of the pipeline but executes the
/// selected endpoint in the trailing <c>UseEndpoints</c>, so short-circuiting here still runs ahead of
/// <c>/rpc</c>, <c>/api/*</c>, the static files and the SPA fallback.
/// </para>
/// <para>
/// <b>When the provider is inactive</b> — disabled, or Caddy/Cloudflare selected — the route table is
/// <see cref="ProxyRouteTableSnapshot.Empty"/> and every request costs one failed dictionary lookup before
/// falling through. That is why it is registered unconditionally rather than behind the option: the
/// provider is switchable at runtime, and a pipeline is not.
/// </para>
/// </remarks>
public sealed class YarpHostDispatchMiddleware(
    RequestDelegate next,
    ProxyRouteTable table,
    IHttpForwarder forwarder,
    ProxyForwardHttpClient client,
    IOptionsMonitor<WatchtowerOptions> options,
    ILogger<YarpHostDispatchMiddleware> logger) {
    /// <summary>
    /// The reserved prefix as a <see cref="PathString"/>, matched by segment so a path merely
    /// <em>starting</em> with those characters (<c>/.watchtowerish</c>) is forwarded like any other.
    /// </summary>
    private static readonly PathString ReservedPrefix =
        new(RouteAccessPolicy.ReservedPathPrefix.TrimEnd('/'));

    /// <summary>Caddy's <c>forward_auth</c> hop header for the original method. Meaningless in process.</summary>
    private const string ForwardedMethodHeader = "X-Forwarded-Method";

    /// <summary>Caddy's <c>forward_auth</c> hop header for the original URI. Meaningless in process.</summary>
    private const string ForwardedUriHeader = "X-Forwarded-Uri";

    public async Task InvokeAsync(HttpContext context) {
        ArgumentNullException.ThrowIfNull(context);
        var request = context.Request;

        // The one reading of "which host is this?", derived once and used for the lookup, the redirect, the
        // access decision and everything the upstream is told — three slightly different renderings of the
        // same name is how a request ends up authorised as one host and forwarded as another. Kestrel has
        // already split the port off; the trailing dot of a fully-qualified name is dropped because a
        // browser sending `app.example.invalid.` means the very same host, and leaving it on would miss the
        // table and quietly hand the tenant's domain to Watchtower's own pipeline. Case needs no work — the
        // table is case-insensitive.
        var host = request.Host.Host.TrimEnd('.');

        // A miss is the common case on the published management port and for any host nobody routed, and it
        // is simply not our request.
        if (!table.Current.TryGet(host, out var row)) {
            await next(context);
            return;
        }

        // A realm's login page. Watchtower serves it itself — forwarding it would be forwarding to
        // ourselves — so it takes the ordinary pipeline, SPA and all. The one thing it does get is the
        // upgrade, exactly as Caddy's self-route did: a login page reached over plain HTTP would set the
        // session cookie without its Secure attribute, and every app redirects visitors here.
        if (row.Local) {
            if (WantsUpgrade(row, request)) {
                RedirectToHttps(context, host);
                return;
            }
            await next(context);
            return;
        }

        var scheme = request.IsHttps ? "https" : "http";
        var clientIp = context.Connection.RemoteIpAddress?.ToString();

        // Defense in depth, on every route and before anything else looks at this request: the full identity
        // vocabulary of both ecosystems we adopted is removed, so nothing a client sent under one of those
        // names can reach the upstream — or Watchtower's own endpoints on the reserved prefix below.
        // Unconditional, including on a JWT-only route, because a group-aware application would honour
        // Remote-Groups whatever this route's mode says (design.md §2.3).
        foreach (var name in IdentityForwarding.StripHeaderNames) request.Headers.Remove(name);
        // The two headers Caddy's forward_auth hop invents to describe the original request. In process
        // there is no such hop — the real method and URI are right here — so a client sending them is
        // either confused or trying to be read as the proxy. Removed rather than ignored, so nothing
        // downstream can be tempted to trust them either.
        request.Headers.Remove(ForwardedMethodHeader);
        request.Headers.Remove(ForwardedUriHeader);

        // Watchtower's own plumbing on the app's domain: the code-redemption callback, per-app sign-out and
        // UserInfo. Served here rather than forwarded, exactly as Caddy's `handle /.watchtower/*` block
        // does, and stamped with the transport headers because the callback binds the code to the domain it
        // is redeemed on by reading X-Forwarded-Host. Ahead of the HTTPS redirect and the access check on
        // purpose: these paths are how a visitor stops being anonymous.
        if (request.Path.StartsWithSegments(ReservedPrefix)) {
            Forwarded.Stamp(request, host, scheme, clientIp);
            await next(context);
            return;
        }

        if (WantsUpgrade(row, request)) {
            RedirectToHttps(context, host);
            return;
        }

        IReadOnlyList<KeyValuePair<string, string>> identity = [];
        if (row.Protected) {
            var verifier = context.RequestServices.GetRequiredService<AccessVerifier>();
            var decision = await verifier.DecideAsync(new AccessRequest(
                Host: host,
                OriginalUri: request.Path + request.QueryString,
                AccessCookie: request.Cookies[AuthSessionService.AccessCookieName],
                // The real method, never X-Forwarded-Method: that header is Caddy's way of telling the
                // verify endpoint what the original request was, and in process it is just a string the
                // client wrote. Honouring it would let a POST present itself as a navigation and collect a
                // login redirect instead of the 401 a non-navigation is owed.
                IsBrowserNavigation: AccessPresentation.IsBrowserNavigation(context, trustForwardedMethod: false),
                ClientDescription: AccessPresentation.Describe(context)), context.RequestAborted);

            switch (decision) {
                case AccessDecision.Allow allow:
                    identity = allow.Headers;
                    break;
                case AccessDecision.Pass:
                    break;
                case AccessDecision.RedirectToLogin redirect:
                    context.Response.Redirect(redirect.Url);
                    return;
                case AccessDecision.Unauthorized:
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                case AccessDecision.Denied denied:
                    await WriteDenialAsync(context, denied);
                    return;
                case AccessDecision.NotFound:
                    // The table says this host is a route and the verifier says it is not. Only a race
                    // between a deletion and this request produces that, and the safe reading of a
                    // disagreement about whether an app exists is that it does not.
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                default:
                    // A verdict this transport was never taught. Failing loudly rather than falling through
                    // to the forward, which would be "let it in" — the one direction an unhandled access
                    // decision must never resolve to.
                    throw new InvalidOperationException($"Unhandled access decision {decision.GetType().Name}.");
            }
        }

        // Kestrel's 30 MB default body cap is a limit on requests to *Watchtower*, and applying it to a
        // proxied upload would mean an application behind the proxy silently losing large uploads it
        // handles fine on its own. The upstream is the one that gets to have an opinion about size.
        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = null;

        // Plain http to the container alias on the stack's ingress network: the hop is inside a private
        // Docker network and the upstream is an ordinary application container that was never asked to hold
        // a certificate. TLS terminated at Kestrel a moment ago; row.Tls describes the visitor's leg.
        var error = await forwarder.SendAsync(
            context,
            $"http://{row.UpstreamHost}:{row.UpstreamPort}",
            client.Invoker,
            ProxyForwardHttpClient.RequestConfig,
            new WatchtowerForwarderTransformer(host, scheme, clientIp, identity));

        if (error == ForwarderError.None) return;

        // Logged whatever state the response is in: a failure part-way through a streamed response is the
        // one an operator most wants to see, and it is precisely the case where nothing can be said to the
        // visitor any more.
        logger.LogWarning(
            "Forwarding {Host} to {Upstream}:{Port} failed: {Error}.",
            host, row.UpstreamHost, row.UpstreamPort, error);

        // A container that is down, restarting, or not yet on the ingress network. 502 is the honest answer
        // — the request was fine, the upstream was not — and the detail stays in the log rather than in a
        // body that would describe the internal topology to the visitor. Only over an untouched 200,
        // though: the forwarder sets a more specific status for the failures it can name (504 on a timeout,
        // 502 with a reason of its own), and overwriting that would flatten a diagnosis into a default.
        if (!context.Response.HasStarted && context.Response.StatusCode == StatusCodes.Status200OK)
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
    }

    /// <summary>
    /// Whether this request should be sent back over HTTPS instead of being served: a TLS route reached
    /// over plain HTTP, with the upgrade left switched on. Off is the escape hatch for a deployment fronted
    /// by another TLS terminator, where redirecting again would loop between the two.
    /// </summary>
    private bool WantsUpgrade(ProxyRouteSnapshot row, HttpRequest request) =>
        row.Tls && !request.IsHttps && options.CurrentValue.Proxy.Yarp.RedirectHttpToHttps;

    /// <summary>
    /// Sends the visitor to the same path on HTTPS. Rebuilt from the route's own host rather than echoed
    /// from the request line, and without a port: the TLS listener is on 443, and carrying the plain-HTTP
    /// port across would name a listener that does not speak TLS.
    /// </summary>
    private static void RedirectToHttps(HttpContext context, string host) {
        var request = context.Request;
        context.Response.Redirect($"https://{host}{request.PathBase}{request.Path}{request.QueryString}");
    }

    /// <summary>
    /// Renders a denial with the same markup the verify endpoint answers Caddy with — one page, one
    /// renderer, so the two transports cannot describe the same refusal differently.
    /// </summary>
    private static Task WriteDenialAsync(HttpContext context, AccessDecision.Denied denied) {
        // The decision states its message as plain text; escaping it is the renderer's job.
        var body = Encoding.UTF8.GetBytes(
            AccessPresentation.Html(denied.Title, AccessPresentation.Encode(denied.Message), denied.Hint));
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = body.Length;
        return context.Response.Body.WriteAsync(body, context.RequestAborted).AsTask();
    }
}

/// <summary>Pipeline registration for <see cref="YarpHostDispatchMiddleware"/>.</summary>
public static class YarpHostDispatchMiddlewareExtensions {
    /// <summary>
    /// Dispatches requests for routed hosts to their containers. Must be registered <em>before</em>
    /// <c>UseForwardedHeaders</c> and after <see cref="AcmeChallengeMiddlewareExtensions.UseAcmeHttpChallenge"/>
    /// — see the middleware's own remarks for why each ordering is load-bearing.
    /// </summary>
    public static IApplicationBuilder UseYarpHostDispatch(this IApplicationBuilder app) =>
        app.UseMiddleware<YarpHostDispatchMiddleware>();
}
