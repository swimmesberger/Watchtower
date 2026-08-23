using System.Collections.Frozen;

namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// What the host actually managed to bind for the in-process proxy — ADR-0022. Read by
/// <see cref="YarpProxyProvider"/> for its running state, by the Settings config surface (which warns when
/// the proxy is enabled but 443 never came up), and — per request — by the host dispatcher, which decides
/// from <see cref="IngressPorts"/> whether it is looking at public ingress or the management plane.
/// </summary>
/// <remarks>
/// Binding 443 can fail for mundane reasons — the port is taken, the container did not publish it,
/// the process lacks the privilege — and the failure is silent from the route table's point of view:
/// routes still resolve, they are just served over plain HTTP. Recording the outcome is what lets the
/// UI say so instead of leaving an operator to discover it from a browser warning.
/// <para>
/// Written from the startup path and read from request and background threads afterwards, so plain
/// properties are enough for most of it; there is no read-modify-write to race over.
/// <see cref="IngressPorts"/> is the exception on both counts — it is written twice (seeded from
/// configuration before the server binds, then narrowed to what actually bound) and it is read on every
/// request to make a security decision, so it is published as one immutable snapshot through a volatile
/// write rather than left to whatever a reader's cache happens to hold.
/// </para>
/// </remarks>
public sealed class YarpListenerState {
    /// <summary>True once the HTTPS endpoint is bound and serving.</summary>
    public bool HttpsBound { get; set; }

    /// <summary>
    /// The plain-HTTP address the host bound, for logs and the ACME self-check. The <c>ProxyHttp</c>
    /// ingress listener when there is one — that is where the CA's HTTP-01 request actually arrives —
    /// and the management endpoint otherwise.
    /// </summary>
    public string? LocalHttpAddress { get; set; }

    private FrozenSet<int> _ingressPorts = FrozenSet<int>.Empty;

    /// <summary>
    /// The local ports that carry <em>ingress</em>: the <c>ProxyHttp</c> and <c>ProxyHttps</c> endpoints.
    /// Seeded from configuration before the server binds and narrowed afterwards to the ports that really
    /// came up. Empty when neither endpoint is configured — a single-endpoint host, where there is no
    /// separation to enforce.
    /// </summary>
    /// <remarks>
    /// The dispatcher reads this per request to answer a question no host header can: did this arrive on a
    /// listener the operator published to the world, or on the management endpoint? A host nobody routed is
    /// a 404 on the first and Watchtower's own UI on the second, which is the whole point of splitting the
    /// endpoints in the first place (ADR-0022). Frozen on assignment: the set is read far more often than
    /// it is written — twice, both before the first request is served — and a reader must never see it
    /// half-built.
    /// </remarks>
    public IReadOnlySet<int> IngressPorts {
        get => Volatile.Read(ref _ingressPorts);
        set => Volatile.Write(
            ref _ingressPorts,
            value as FrozenSet<int> ?? value?.ToFrozenSet() ?? FrozenSet<int>.Empty);
    }

    /// <summary>The port of the management endpoint (<c>Http</c>), for logs and diagnostics.</summary>
    public int? ManagementPort { get; set; }
}
