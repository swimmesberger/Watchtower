using System.Collections.Frozen;

namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// What the in-process proxy's ingress listeners currently are — ADR-0022. Read by
/// <see cref="YarpProxyProvider"/> for its running state, by the Settings config surface (which says so
/// when TLS ingress is off), and — per request — by the host dispatcher, which decides from
/// <see cref="YarpListenerSnapshot.IngressPorts"/> whether it is looking at public ingress or the
/// management plane.
/// </summary>
/// <remarks>
/// <para>
/// Since the ingress endpoints follow the reverse-proxy settings, this is no longer written once at
/// startup: enabling the proxy, switching provider or moving a port re-projects the Kestrel section and
/// republishes the facts here. The four of them are one reading of one moment — a request that saw the new
/// ingress ports and the old management port would be reading a state that never existed — so they are
/// published together as an immutable <see cref="YarpListenerSnapshot"/> swapped in with a single volatile
/// write, rather than as four independently settable properties.
/// </para>
/// <para>
/// The snapshot is <em>configuration</em> truth: what the projected section asks Kestrel to bind. A bind
/// that then fails (the port is taken, the container did not publish it) is logged by Kestrel and does not
/// walk back the snapshot. That is deliberately the wider of the two readings for the one consumer where
/// it is a security decision: no request can arrive on a port nothing is listening to, whereas a bound
/// port missing from <c>IngressPorts</c> is the dispatcher's fall-through rule in force on a port
/// published to the internet.
/// </para>
/// </remarks>
public sealed class YarpListenerState {
    private YarpListenerSnapshot _current = new();

    /// <summary>The current facts, as one consistent reading. Cheap — take it once per request.</summary>
    public YarpListenerSnapshot Current => Volatile.Read(ref _current);

    /// <summary>Swaps in a new reading. Readers see the old one or the new one, never a mixture.</summary>
    public void Publish(YarpListenerSnapshot snapshot) {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }

    /// <summary>Publishes a change to the current reading — the convenience form of <see cref="Publish"/>.</summary>
    public void Update(Func<YarpListenerSnapshot, YarpListenerSnapshot> change) {
        ArgumentNullException.ThrowIfNull(change);
        Publish(change(Current));
    }

    /// <inheritdoc cref="YarpListenerSnapshot.HttpsBound"/>
    public bool HttpsBound => Current.HttpsBound;

    /// <inheritdoc cref="YarpListenerSnapshot.LocalHttpAddress"/>
    public string? LocalHttpAddress => Current.LocalHttpAddress;

    /// <inheritdoc cref="YarpListenerSnapshot.IngressPorts"/>
    public IReadOnlySet<int> IngressPorts => Current.IngressPorts;

    /// <inheritdoc cref="YarpListenerSnapshot.ManagementPort"/>
    public int? ManagementPort => Current.ManagementPort;
}

/// <summary>One consistent reading of the in-process proxy's listeners.</summary>
public sealed record YarpListenerSnapshot {
    private readonly FrozenSet<int> _ingressPorts = FrozenSet<int>.Empty;

    /// <summary>True when the TLS ingress endpoint is configured — routes can be served over HTTPS.</summary>
    public bool HttpsBound { get; init; }

    /// <summary>
    /// The plain-HTTP address this process can dial itself, for logs and the ACME self-check. The
    /// <c>ProxyHttp</c> ingress listener when there is one — that is where the CA's HTTP-01 request
    /// actually arrives — and the management endpoint otherwise.
    /// </summary>
    public string? LocalHttpAddress { get; init; }

    /// <summary>
    /// The local ports that carry <em>ingress</em>: the <c>ProxyHttp</c> and <c>ProxyHttps</c> endpoints.
    /// Empty when the in-process proxy is not the active provider, or when both ports are turned off — a
    /// single-listener host, where there is no separation to enforce.
    /// </summary>
    /// <remarks>
    /// The dispatcher reads this per request to answer a question no host header can: did this arrive on a
    /// listener the operator published to the world, or on the management endpoint? A host nobody routed is
    /// a 404 on the first and Watchtower's own UI on the second, which is the whole point of splitting the
    /// endpoints (ADR-0022). Frozen on assignment: it is read on every request and a reader must never see
    /// it half-built.
    /// </remarks>
    public IReadOnlySet<int> IngressPorts {
        get => _ingressPorts;
        init => _ingressPorts = value as FrozenSet<int> ?? value?.ToFrozenSet() ?? FrozenSet<int>.Empty;
    }

    /// <summary>The port of the management endpoint (<c>Http</c>), for logs and diagnostics.</summary>
    public int? ManagementPort { get; init; }
}
