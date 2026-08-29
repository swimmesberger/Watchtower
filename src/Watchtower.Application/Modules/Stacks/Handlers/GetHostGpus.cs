using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>
/// Reports the Docker host's GPU render nodes (ADR-0031) — what the Settings tab shows next to the
/// "map host GPU(s)" control, so an operator sees what the intent would resolve to on this host
/// before deploying. Served from <see cref="HostGpuProbe"/>'s short-lived cache; a probe failure is
/// data (<see cref="Response.Error"/>), not an RPC error, because "we could not look" is a state the
/// UI must render rather than toast away.
/// </summary>
[Handler("stacks.hostGpus")]
public sealed class GetHostGpus(HostGpuProbe probe)
    : IHandler<GetHostGpus.Query, Result<GetHostGpus.Response>> {
    public sealed record Query;
    /// <param name="Gpus">The render nodes found; empty on a GPU-less host.</param>
    /// <param name="Error">Why the probe could not run, or null when it did.</param>
    /// <param name="Nvidia">
    /// The NVIDIA route's state, which the render-node listing cannot express: a card is usually
    /// absent from <paramref name="Gpus"/> and is reserved through the toolkit instead (ADR-0032).
    /// </param>
    public sealed record Response(IReadOnlyList<HostGpuDto> Gpus, string? Error, HostNvidiaDto Nvidia);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var catalog = await probe.GetAsync(ct);
        return new Response(
            [.. catalog.Gpus.Select(g => new HostGpuDto(
                g.Name, g.Path, VendorLabel(g.VendorId), g.Driver, g.PciAddress, g.IsMappable))],
            catalog.Error,
            new HostNvidiaDto(catalog.NvidiaPresent, catalog.NvidiaRuntimeAvailable));
    }

    /// <summary>The label the UI prints; the raw id stays server-side.</summary>
    private static string VendorLabel(string vendorId) => vendorId switch {
        HostGpu.IntelVendorId => "intel",
        HostGpu.AmdVendorId => "amd",
        HostGpu.NvidiaVendorId => "nvidia",
        _ => "unknown",
    };
}
