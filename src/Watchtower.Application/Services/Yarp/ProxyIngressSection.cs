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
    /// The last computed set together with the token that was current when it was read, or null before
    /// the first read. Volatile because it is written by whichever thread recomputed and read by every
    /// request thread.
    /// </summary>
    private volatile Reading? _reading;

    /// <summary>A set and the reload token it is only valid for. One field, so the two cannot part company.</summary>
    private sealed record Reading(IChangeToken Token, FrozenSet<int> Ports);

    public ProxyIngressSection(IConfiguration section) {
        ArgumentNullException.ThrowIfNull(section);
        _section = section;
    }

    /// <summary>
    /// The ports the projection currently gives a port route a listener on. A frozen-set lookup and one
    /// boolean per call once warm, which is what lets the request path ask it on every request rather
    /// than only on a rare disagreement — including on a deployment where a port route's row permanently
    /// names a port the projection refuses to bind, where the answer never changes and the question is
    /// asked constantly.
    /// </summary>
    /// <remarks>
    /// The cached set carries the reload token it was read under, and every read checks it. That pairing
    /// is what makes the cache correct without a lock. Under an invalidation callback and a bare set the
    /// two steps could interleave: a thread that read the section before the projection assigned its new
    /// data would still see an unchanged token, and could publish that stale reading <em>after</em> the
    /// callback had cleared the cache — pinning ports that no longer exist until the next reload, which
    /// on a converged deployment may never come. Here a stale entry cannot outlive its token: the very
    /// next reader sees <c>HasChanged</c> and recomputes.
    /// <para>
    /// The token is still taken before the section is read and checked after it, so a reading that loses
    /// the race is handed to its own caller and simply not cached.
    /// </para>
    /// </remarks>
    public IReadOnlySet<int> BoundPortRoutePorts() {
        if (_reading is { } cached && !cached.Token.HasChanged) return cached.Ports;

        var token = _section.GetReloadToken();
        var ports = PortRouteListeners.BoundPorts(_section).ToFrozenSet();
        if (!token.HasChanged) _reading = new Reading(token, ports);
        return ports;
    }
}
