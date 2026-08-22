namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// What the host actually managed to bind for the in-process proxy — ADR-0017 (forthcoming). Filled in
/// once at <c>ApplicationStarted</c> from <c>IServerAddressesFeature</c>; read by
/// <see cref="YarpProxyProvider"/> for its running state and by the Settings config surface, which
/// warns when the proxy is enabled but 443 never came up.
/// </summary>
/// <remarks>
/// Binding 443 can fail for mundane reasons — the port is taken, the container did not publish it,
/// the process lacks the privilege — and the failure is silent from the route table's point of view:
/// routes still resolve, they are just served over plain HTTP. Recording the outcome is what lets the
/// UI say so instead of leaving an operator to discover it from a browser warning.
/// <para>
/// Written once from the startup callback and read from request and background threads afterwards, so
/// plain properties are enough; there is no read-modify-write to race over.
/// </para>
/// </remarks>
public sealed class YarpListenerState {
    /// <summary>True once the HTTPS endpoint is bound and serving.</summary>
    public bool HttpsBound { get; set; }

    /// <summary>The plain-HTTP address the host bound, for logs and the ACME self-check.</summary>
    public string? LocalHttpAddress { get; set; }
}
