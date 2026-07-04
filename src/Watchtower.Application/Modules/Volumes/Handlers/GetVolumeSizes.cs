using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Volumes.Handlers;

/// <summary>
/// Returns on-disk sizes per volume from <c>GET /system/df</c> (expensive — on-demand only, never
/// polled). Only volumes whose size Docker has computed are returned; a missing name means unknown,
/// not zero. <c>refCount</c> is computed the same way as <c>volumes.list</c> (containers in any
/// state) so the merged view stays consistent. When <c>project</c> is set, sizes are filtered to
/// that compose project's volumes.
/// </summary>
[Handler("volumes.sizes")]
public sealed class GetVolumeSizes(DockerEngineClient docker)
    : IHandler<GetVolumeSizes.Query, Result<GetVolumeSizes.Response>> {
    public sealed record Query(string? Project);
    public sealed record Response(IReadOnlyList<VolumeSizeDto> Sizes);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        try {
            var sizes = await docker.GetVolumeSizesAsync(ct);
            var containers = await docker.ListAllContainersAsync(ct);
            var inUse = VolumeReferences.BuildInUseMap(containers);

            // Project filtering needs the volume labels; only fetch the list when a filter is set.
            HashSet<string>? allowed = null;
            if (query.Project is { } filter) {
                var volumes = await docker.ListVolumesAsync(ct);
                allowed = volumes
                    .Where(v => (v.Labels ?? []).TryGetValue(VolumeReferences.ComposeProjectLabel, out var p)
                                && string.Equals(p, filter, StringComparison.Ordinal))
                    .Select(v => v.Name)
                    .ToHashSet(StringComparer.Ordinal);
            }

            var items = new List<VolumeSizeDto>(sizes.Count);
            foreach (var (name, sizeBytes) in sizes) {
                if (allowed is not null && !allowed.Contains(name)) continue;
                var refCount = inUse.TryGetValue(name, out var names) ? names.Count : 0;
                items.Add(new VolumeSizeDto(name, sizeBytes, refCount));
            }

            return new Response(items);
        } catch (HttpRequestException ex) {
            return AppError.Internal($"Docker Engine API error: {ex.Message}");
        }
    }
}
