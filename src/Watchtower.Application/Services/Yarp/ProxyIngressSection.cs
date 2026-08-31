using Microsoft.Extensions.Configuration;

namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// The projected Kestrel section, made resolvable — the configuration Kestrel itself reads to decide
/// which listeners exist (ADR-0022, ADR-0033).
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
public sealed class ProxyIngressSection(IConfiguration section) {
    /// <summary>
    /// The ports the projection currently gives a port route a listener on. Enumerates the section, so it
    /// belongs on a path that runs rarely — the listener state is the cached reading for everything else.
    /// </summary>
    public IReadOnlySet<int> BoundPortRoutePorts() => PortRouteListeners.BoundPorts(section);
}
