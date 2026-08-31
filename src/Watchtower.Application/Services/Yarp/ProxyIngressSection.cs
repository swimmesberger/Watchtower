using System.Collections.Frozen;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// The projected Kestrel section, made resolvable — the configuration Kestrel itself reads to decide
/// which listeners exist (ADR-0022, ADR-0033) — with the one question the request path asks of it
/// answered from a cache.
/// </summary>
/// <remarks>
/// <para>
/// It exists because the section is built before the container is, so it cannot be resolved the way the
/// rest of the proxy plane is; the host constructs one of these and registers it. A holder rather than an
/// <c>IConfiguration</c> registration, which would shadow the application's own configuration for every
/// other consumer in the process.
/// </para>
/// <para>
/// The one thing it answers is deliberately narrow. Passing the raw section around would invite reading
/// arbitrary keys off it at request time; what a consumer actually needs is the authoritative answer to
/// "did the projection give a port route a listener on this port?", which is
/// <see cref="PortRouteListeners.BoundPorts"/> over exactly the data Kestrel used.
/// </para>
/// </remarks>
public sealed class ProxyIngressSection {
    private readonly IConfiguration _section;

    /// <summary>
    /// The last computed set, or null when it has to be computed again. Volatile because it is written by
    /// whichever thread notices the invalidation and read by every request thread.
    /// </summary>
    private volatile FrozenSet<int>? _ports;

    public ProxyIngressSection(IConfiguration section) {
        ArgumentNullException.ThrowIfNull(section);
        _section = section;
        // Held for the life of the process, like the configuration it watches — there is nothing to
        // unsubscribe from before the host itself goes away. Invalidation only: the recompute happens on
        // the next read, so a settings write that touches no listener costs one null assignment.
        ChangeToken.OnChange(section.GetReloadToken, () => _ports = null);
    }

    /// <summary>
    /// The ports the projection currently gives a port route a listener on. A frozen-set lookup per call
    /// once warm, which is what lets the request path ask it on every request rather than only on a rare
    /// disagreement — including on a deployment where a port route's row permanently names a port the
    /// projection refuses to bind, where the answer never changes and the question is asked constantly.
    /// </summary>
    /// <remarks>
    /// The reload token is taken <em>before</em> the section is read and checked after it. The projection
    /// assigns its data before raising that token, so a token that has not changed across the read proves
    /// the reading is current; a reload that lands mid-read would otherwise let the older reading be
    /// published over the invalidation and stay cached until the next one. A reading that loses that race
    /// is returned to its own caller and simply not cached, so the next call recomputes.
    /// </remarks>
    public IReadOnlySet<int> BoundPortRoutePorts() {
        if (_ports is { } cached) return cached;

        var token = _section.GetReloadToken();
        var ports = PortRouteListeners.BoundPorts(_section).ToFrozenSet();
        if (!token.HasChanged) _ports = ports;
        return ports;
    }
}
