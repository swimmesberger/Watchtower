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
/// The in-process proxy's request path — ADR-0022. Every request is dispatched by its
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
/// <b>Ingress and management are different listeners.</b> The dispatch is by host <em>and</em> by local
/// port — see <see cref="IsIngress"/> for which port counts as which and why the rule is stated by
/// exclusion. On an ingress port a host nobody routed gets a
/// bare 404 rather than falling through, because falling through on a port published to the internet is
/// how <c>http://&lt;public-ip&gt;/</c> ends up serving the management plane — the login page with
/// authentication on, the whole UI with it off. The mirror rule holds on the management port: a routed
/// application's host is refused there too, so ingress traffic cannot be half-served on the endpoint an
/// operator is meant to bind privately. Watchtower's own hosts (ADR-0023) are served on both, which is
/// what keeps the UI reachable — through ingress on the hostnames the operator routed there, and on the
/// management endpoint whatever they are.
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
    YarpListenerState listener,
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

    /// <summary>
    /// Whether a connection arrived on public ingress rather than on the management plane. This is a
    /// security decision, so it is stated by <em>exclusion</em>: once we know which port the management
    /// plane is on, everything else is ingress.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The matrix, in order:
    /// </para>
    /// <list type="table">
    /// <listheader>
    ///   <term>Management port / ingress ports</term><description>Verdict</description>
    /// </listheader>
    /// <item>
    ///   <term>no ingress ports configured</term>
    ///   <description><b>Never ingress.</b> The in-process proxy is off, or both its ports are, so there
    ///   is no public plane to separate from — this is the single-listener shape, and it includes every
    ///   development host, the Aspire AppHost and a Caddy or Cloudflare deployment. Refusing hosts here
    ///   would refuse them everywhere. Checked first, so a dev host that also binds an
    ///   <c>https://</c> hosting URL does not have that second listener read as ingress.</description>
    /// </item>
    /// <item>
    ///   <term>management port known, ingress configured</term>
    ///   <description><b>Ingress unless it is the management port.</b> Fail closed: a port we did not
    ///   expect is treated as public, not as the management plane.</description>
    /// </item>
    /// <item>
    ///   <term>management port unknown</term>
    ///   <description>Fall back to the configured ingress set. Only reachable where the state was never
    ///   derived from a real projection — <c>TestServer</c> and the unit hosts — because a projection
    ///   that derives ingress always carries a management endpoint with it.</description>
    /// </item>
    /// </list>
    /// <para>
    /// The exclusion rule is what makes a <em>stale</em> listener safe. Kestrel keeps its existing
    /// endpoints when a rebind fails, so moving an ingress port onto one something else holds leaves the
    /// old port bound and serving while configuration no longer names it. Under the old set-membership
    /// rule that port became "management" and an unrouted host on it fell through to Watchtower's own UI —
    /// the exact exposure the endpoint split exists to prevent. Under this rule it stays ingress.
    /// </para>
    /// <para>
    /// The cost is that any <em>other</em> endpoint an operator adds to <c>Kestrel:Endpoints:*</c> counts
    /// as ingress too: unknown hosts get a 404 there and only routed hosts are served. That is the safe
    /// default — the failure is a listener that serves less than intended, not one that serves the
    /// management plane to the internet.
    /// </para>
    /// </remarks>
    internal static bool IsIngress(YarpListenerSnapshot listeners, int localPort) {
        if (listeners.IngressPorts.Count == 0) return false;
        return listeners.ManagementPort is { } management
            ? localPort != management
            : listeners.IngressPorts.Contains(localPort);
    }

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

        // Which listener did this arrive on? The Host header cannot answer this and neither can the
        // scheme — only the local port can, which is why the listener state records it. One reading of the
        // facts for the whole request: they change at runtime (the ingress endpoints follow the
        // reverse-proxy settings), and deciding "is this ingress?" against one snapshot and "is there
        // ingress at all?" against another is how a request gets judged by a state that never existed.
        var listeners = listener.Current;
        var isIngress = IsIngress(listeners, context.Connection.LocalPort);

        if (!table.Current.TryGet(host, out var row)) {
            // On ingress, a host nobody routed is a stranger: a scanner on the public IP, or a domain
            // someone pointed here. It gets nothing — not Watchtower's login page, and certainly not the
            // whole management SPA when authentication is off. This is the invariant Caddy used to hold by
            // simply not having a site block for it.
            if (isIngress) {
                NotFound(context);
                return;
            }

            // On the management endpoint a miss is the ordinary case — this is Watchtower's own UI.
            await next(context);
            return;
        }

        // A Watchtower route (ADR-0023). Watchtower serves it itself — forwarding it would be forwarding
        // to ourselves — so it takes the ordinary pipeline, SPA and all, on either kind of listener:
        // through ingress it is a hostname the management plane is deliberately reachable on, and on the
        // management endpoint it is how an operator who bound 8080 privately still reaches the UI. The one
        // thing it does get is the upgrade: a login page reached over plain HTTP would set the session
        // cookie without its Secure attribute, and every protected app redirects visitors here.
        if (row.Local) {
            if (WantsUpgrade(row, request)) {
                RedirectToHttps(context, host);
                return;
            }
            await next(context);
            return;
        }

        // A routed application's host, arriving on the management endpoint. Half-serving it there — the
        // access check and the forward, on a port whose whole point is that it is not ingress — is a second
        // way in that nobody published, so it gets the same nothing an unrouted host gets on ingress.
        // The IngressPorts guard is redundant with IsIngress above (nothing is ingress when the set is
        // empty, so !isIngress would otherwise refuse routes on a single-listener host) and kept as the
        // statement of that rule where it is being relied on.
        if (!isIngress && listeners.IngressPorts.Count > 0) {
            NotFound(context);
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
    /// The answer to a request that is not this listener's to serve. Bare: no body, no reason, no
    /// <c>Server</c>-side hint about what else might be here. "Nothing is at this name" is the only thing a
    /// stranger on the ingress port is owed, and any detail beyond it would turn the refusal into an oracle.
    /// </summary>
    private static void NotFound(HttpContext context) =>
        context.Response.StatusCode = StatusCodes.Status404NotFound;

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
