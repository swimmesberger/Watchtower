namespace Watchtower.Api.Proxy;

/// <summary>
/// The names and the values of the transport headers Watchtower states about a forwarded request, in the
/// one place both callers read them from: <see cref="WatchtowerForwarderTransformer"/>, which writes them
/// onto the outgoing <see cref="HttpRequestMessage"/>, and <see cref="YarpHostDispatchMiddleware"/>'s
/// <c>/.watchtower/*</c> fall-through, which writes them onto the inbound <see cref="HttpRequest"/> because
/// that request is about to be handled by Watchtower's own endpoints instead of being forwarded.
/// </summary>
/// <remarks>
/// All three are <em>set</em>, never appended to. Watchtower is the first hop — it terminates the
/// connection from the visitor — so there is no upstream proxy whose chain we would be extending, and an
/// inbound value can only have come from the client. Appending would let a client prepend a hop of its own
/// choosing and have the upstream read it as the origin address; overwriting makes the client's version
/// unobservable, which is the only safe reading of a header the client can write.
/// <para>
/// These three and no others. RFC 7239's <c>Forwarded</c> header and the de-facto <c>X-Real-IP</c> are
/// passed through to the upstream exactly as the client sent them — which is what the Caddy provider does
/// too, since <see cref="Application.Services.IdentityForwarding.StripHeaderNames"/> enumerates identity
/// names and neither of those is one. That is a deliberate limit on the claim being made here rather than
/// an oversight: an upstream that reads its client address from anything but <c>X-Forwarded-For</c> is
/// reading a value Watchtower has not vouched for, under either provider, and the place to fix that is one
/// strip list shared by both — not a second, quieter policy on this path only.
/// </para>
/// </remarks>
internal static class Forwarded {
    /// <summary>The host the visitor asked for, which is what the app knows itself by.</summary>
    public const string Host = "X-Forwarded-Host";

    /// <summary>The scheme of the visitor's <em>connection</em> to Kestrel, never an inbound claim about it.</summary>
    public const string Proto = "X-Forwarded-Proto";

    /// <summary>The visitor's address, as the socket reports it.</summary>
    public const string For = "X-Forwarded-For";

    /// <summary>
    /// Stamps the three headers onto an inbound request that Watchtower is about to handle itself. Used for
    /// the <c>/.watchtower/*</c> paths on an app's own domain: the callback endpoint binds the login code to
    /// the domain it is being redeemed on, and reads that domain off <c>X-Forwarded-Host</c> because with
    /// Caddy that is where it comes from. In process the same fact is on the connection, so this is the
    /// adapter that puts it where the endpoint looks.
    /// </summary>
    /// <param name="host">
    /// The normalised host the dispatcher matched, passed in rather than re-read off the request so the
    /// callback is told the same name the route table was looked up with.
    /// </param>
    public static void Stamp(HttpRequest request, string host, string scheme, string? clientIp) {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers[Host] = host;
        request.Headers[Proto] = scheme;
        if (string.IsNullOrEmpty(clientIp)) request.Headers.Remove(For);
        else request.Headers[For] = clientIp;
    }
}
