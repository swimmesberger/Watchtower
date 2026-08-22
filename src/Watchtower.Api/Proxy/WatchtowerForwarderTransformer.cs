using Yarp.ReverseProxy.Forwarder;

namespace Watchtower.Api.Proxy;

/// <summary>
/// How a proxied request is reshaped on its way to the upstream — ADR-0017. Two things the
/// stock <see cref="HttpTransformer"/> would get wrong for Watchtower, plus the verified identity:
/// <list type="number">
///   <item><description>
///     the inbound <c>Host</c> is <b>preserved</b>. YARP's default drops it, so the upstream would see the
///     container alias (<c>project-service</c>) as its own name and mint absolute URLs, cookies and
///     redirects under it. Caddy preserves the original host, every application behind Watchtower was
///     configured against that behaviour, and the header is also what a virtual-hosting upstream routes on.
///   </description></item>
///   <item><description>
///     the <c>X-Forwarded-*</c> transport headers are <b>set</b> from the connection — see
///     <see cref="Forwarded"/> for why setting rather than appending is the security-relevant half.
///   </description></item>
///   <item><description>
///     the identity headers the access decision produced are added last, after the inbound request has
///     already had every forwardable identity name stripped by the dispatcher. What the upstream receives
///     under those names is therefore only ever what <c>AccessVerifier</c> built (design.md §2.3).
///   </description></item>
/// </list>
/// </summary>
/// <param name="originalHost">
/// The host the visitor asked for, as the dispatcher normalised it — the same value that matched the route
/// table, decided the access question and goes into <c>X-Forwarded-Host</c>. Deliberately that rather than
/// the raw <c>Host</c> header: one name throughout is what stops a request being authorised as one host and
/// forwarded as a slightly different spelling of it. The port is not carried across for the same reason the
/// HTTPS redirect drops it — the listener that answered is the one the operator published.
/// </param>
/// <param name="scheme">The scheme of the visitor's connection — <c>https</c> or <c>http</c>.</param>
/// <param name="clientIp">The remote address, or <see langword="null"/> when there is none to state.</param>
/// <param name="identityHeaders">
/// The verified identity, empty for a public route or a bypass path. Order is preserved because
/// <c>AccessVerifier</c> builds it in a defined one.
/// </param>
public sealed class WatchtowerForwarderTransformer(
    string originalHost,
    string scheme,
    string? clientIp,
    IReadOnlyList<KeyValuePair<string, string>> identityHeaders) : HttpTransformer {
    public override async ValueTask TransformRequestAsync(
        HttpContext httpContext,
        HttpRequestMessage proxyRequest,
        string destinationPrefix,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(proxyRequest);

        // The base does the bulk — copies the request headers (Host excepted) and builds the RequestUri from
        // the destination prefix and the original path/query. Called through `base` rather than
        // HttpTransformer.Default so this stays one transformation of one request rather than two objects
        // taking turns on it.
        await base.TransformRequestAsync(httpContext, proxyRequest, destinationPrefix, cancellationToken);

        // Restore what the base deliberately dropped. Assigned through the typed property so an invalid
        // host is rejected here rather than travelling.
        proxyRequest.Headers.Host = originalHost;
        Set(proxyRequest, Forwarded.Host, originalHost);
        Set(proxyRequest, Forwarded.Proto, scheme);
        if (!string.IsNullOrEmpty(clientIp)) Set(proxyRequest, Forwarded.For, clientIp);
        else proxyRequest.Headers.Remove(Forwarded.For);

        foreach (var (name, value) in identityHeaders) Set(proxyRequest, name, value);
    }

    /// <summary>
    /// Replaces a header outright: remove, then add. <c>TryAddWithoutValidation</c> because the values are
    /// Watchtower's own (a host name, a scheme, an address, a minted assertion) and have already been
    /// filtered for header safety where they came from an account.
    /// </summary>
    private static void Set(HttpRequestMessage request, string name, string value) {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }
}
