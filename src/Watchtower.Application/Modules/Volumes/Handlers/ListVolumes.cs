using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Volumes.Handlers;

/// <summary>
/// Lists Docker volumes (cheap, pollable — sizes are omitted; use <c>volumes.sizes</c> for those).
/// Compose association comes from the <c>com.docker.compose.project</c> label; ref-counts and the
/// three-state lifecycle are computed server-side by intersecting all containers' named-volume
/// mounts (running AND stopped). When <c>project</c> is set the list is filtered to that project.
/// </summary>
[Handler("volumes.list")]
public sealed class ListVolumes(DockerEngineClient docker)
    : IHandler<ListVolumes.Query, Result<ListVolumes.Response>> {
    public sealed record Query(string? Project);
    public sealed record Response(IReadOnlyList<VolumeDto> Volumes);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        try {
            var volumes = await docker.ListVolumesAsync(ct);
            // all=true so stopped containers still count toward refCount / inUseBy.
            var containers = await docker.ListAllContainersAsync(ct);
            var inUse = VolumeReferences.BuildInUseMap(containers);

            var items = new List<VolumeDto>(volumes.Count);
            foreach (var v in volumes) {
                // The client normalizes null labels to empty, but the compile-time type is nullable.
                IReadOnlyDictionary<string, string> labels = v.Labels ?? new Dictionary<string, string>();
                var project = labels.TryGetValue(VolumeReferences.ComposeProjectLabel, out var p) ? p : null;

                if (query.Project is { } filter && !string.Equals(project, filter, StringComparison.Ordinal))
                    continue;

                var composeVolume = labels.TryGetValue(VolumeReferences.ComposeVolumeLabel, out var cv) ? cv : null;
                var usedBy = inUse.TryGetValue(v.Name, out var names) ? names : [];
                var refCount = usedBy.Count;

                items.Add(new VolumeDto(
                    v.Name,
                    v.Driver,
                    project,
                    composeVolume,
                    v.Mountpoint,
                    v.CreatedAt,
                    labels,
                    v.Scope,
                    usedBy,
                    refCount,
                    VolumeReferences.ResolveLifecycle(project, refCount)));
            }

            return new Response(items);
        } catch (HttpRequestException ex) {
            return AppError.Internal($"Docker Engine API error: {ex.Message}");
        }
    }
}
