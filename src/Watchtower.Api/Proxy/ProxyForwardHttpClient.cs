using System.Diagnostics;
using System.Net;
using Yarp.ReverseProxy.Forwarder;

namespace Watchtower.Api.Proxy;

/// <summary>
/// The one outbound client every proxied request is forwarded on, and the request configuration that goes
/// with it — ADR-0020. A singleton because connection pooling is the whole point: a client
/// per request would open a new TCP connection to the upstream container for every hit.
/// </summary>
/// <remarks>
/// Every handler setting below is a deliberate <em>subtraction</em>, and for one reason: a reverse proxy is
/// a pipe, not a user agent. Anything the handler does on its own behalf — following a redirect,
/// decompressing a body, keeping a cookie jar — is a decision it would be making instead of the client that
/// actually asked, and the client would never learn it happened. So redirects are returned as the 3xx they
/// are, bodies travel in whatever encoding the upstream chose, and no cookie is ever stored across two
/// visitors who happen to share this pool.
/// <para>
/// The values that are <em>added</em> are the two that a pipe does need: a connect timeout, so an upstream
/// whose container is gone fails in seconds rather than hanging a request; and YARP's
/// <see cref="ReverseProxyPropagator"/>, which suppresses the outgoing <c>traceparent</c> a plain
/// <c>HttpClient</c> would mint for its own activity — the proxy is not the originator of the trace, and
/// stamping itself as one detaches the upstream's spans from the caller's.
/// </para>
/// </remarks>
public sealed class ProxyForwardHttpClient : IDisposable {
    /// <summary>
    /// How the forwarder is asked to shape each request. HTTP/1.1 with
    /// <see cref="HttpVersionPolicy.RequestVersionOrLower"/> rather than YARP's HTTP/2 default: the
    /// upstreams are ordinary containers reached over a private Docker network with no TLS and therefore no
    /// ALPN to negotiate with, and h2c is not something an arbitrary application image can be assumed to
    /// speak. Downgrading is invisible to the visitor — the version they see is the one Kestrel answered on.
    /// </summary>
    public static readonly ForwarderRequestConfig RequestConfig = new() {
        Version = HttpVersion.Version11,
        VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        // An IDLE timeout, not a total one: any activity in either direction resets it, so a slow upload or
        // a long-running report is fine however long it takes, and only a connection that goes quiet for
        // this long is cancelled. That is also the limit a streamed response lives under — an SSE stream or
        // a log tail that says nothing for a hundred seconds is dropped, which is why applications that
        // stream indefinitely send keep-alives.
        ActivityTimeout = TimeSpan.FromSeconds(100),
    };

    private readonly SocketsHttpHandler _handler = new() {
        // A proxy honouring the *host's* HTTP_PROXY would send every upstream hop through it.
        UseProxy = false,
        // A 302 belongs to the client, not to us: following it here would return the wrong URL's body under
        // the original request's status, and the visitor's address bar would never learn of the hop.
        AllowAutoRedirect = false,
        // Bodies pass through byte-for-byte in whatever encoding the upstream chose; decompressing would
        // also mean re-encoding or stripping Content-Encoding, and the response headers are copied verbatim.
        AutomaticDecompression = DecompressionMethods.None,
        // A shared cookie container across all visitors of all routes is exactly the cross-user leak it
        // sounds like. Cookies travel in the copied headers, where they belong to one request.
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        ActivityHeadersPropagator = new ReverseProxyPropagator(DistributedContextPropagator.Current),
        // Inert while RequestConfig pins the upstream hop to HTTP/1.1; kept so raising that version is a
        // one-line change rather than one that also has to rediscover this setting.
        EnableMultipleHttp2Connections = true,
    };

    private readonly HttpMessageInvoker _invoker;

    public ProxyForwardHttpClient() => _invoker = new HttpMessageInvoker(_handler, disposeHandler: false);

    /// <summary>The invoker handed to <see cref="IHttpForwarder.SendAsync(HttpContext, string, HttpMessageInvoker, ForwarderRequestConfig, HttpTransformer)"/>.</summary>
    public HttpMessageInvoker Invoker => _invoker;

    public void Dispose() {
        _invoker.Dispose();
        _handler.Dispose();
    }
}
