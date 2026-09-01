using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// One port route's listen port, as the Watchtower container publishes it (ADR-0033).
/// </summary>
/// <param name="Bound">
/// Whether the container publishes this host port right now. False means the route is served inside the
/// container and nothing outside can reach it — the state the Routes page marks per row.
/// </param>
/// <param name="Managed">
/// Whether Watchtower published it itself. A port the operator declared reads false and is never taken
/// away again, even when its route is deleted.
/// </param>
public sealed record PortBindingDto(int Port, int RouteId, string ServiceName, bool Bound, bool Managed);

/// <summary>
/// Reports whether the port routes' host ports are published on Watchtower's own container, and whether
/// Watchtower may publish the missing ones itself.
/// </summary>
/// <remarks>
/// Its own method rather than a few more fields on <c>proxy.getStatus</c>: answering it inspects the
/// Docker daemon, and the status call is polled by every page that shows the proxy badge.
/// </remarks>
[Handler("proxy.getPortBindings")]
public sealed class GetPortBindings(SelfPortPublishService ports)
    : IHandler<GetPortBindings.Query, Result<GetPortBindings.Response>> {
    public sealed record Query;

    /// <param name="ContainerDetected">
    /// Whether Watchtower can see its own container. False outside Docker, where every port reads
    /// unpublished and the only remedy is the manual one named in <paramref name="UnavailableReason"/>.
    /// </param>
    /// <param name="UnavailableReason">
    /// Why <c>proxy.applyPortBindings</c> would be refused, or null when it would be accepted.
    /// </param>
    /// <param name="LastError">What a previous apply failed with, or null.</param>
    /// <param name="PendingUnpublish">
    /// Ports Watchtower published that no route uses any more, which an apply would release. They have
    /// no row in <paramref name="Ports"/> — the route that put them there is gone.
    /// </param>
    public sealed record Response(
        bool ContainerDetected,
        string? UnavailableReason,
        string? LastError,
        IReadOnlyList<PortBindingDto> Ports,
        IReadOnlyList<int> PendingUnpublish);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var status = await ports.GetStatusAsync(ct);
        return new Response(
            status.ContainerDetected,
            status.UnavailableReason,
            status.LastError,
            [.. status.Ports.Select(p => new PortBindingDto(p.Port, p.RouteId, p.ServiceName, p.Bound, p.Managed))],
            status.PendingUnpublish);
    }
}
