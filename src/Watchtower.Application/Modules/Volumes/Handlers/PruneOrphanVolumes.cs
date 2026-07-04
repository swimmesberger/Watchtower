using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Volumes.Handlers;

/// <summary>
/// Removes every orphaned volume (no compose project label AND zero container references, running or
/// stopped) one by one. Returns the names actually removed and the reclaimed byte total. Reclaimed
/// bytes come from a single <c>/system/df</c> call (summing the known sizes of the removed volumes);
/// it is null only when that df call fails, in which case removal still proceeds.
/// </summary>
[Handler("volumes.pruneOrphans")]
public sealed class PruneOrphanVolumes(DockerEngineClient docker)
    : IHandler<PruneOrphanVolumes.Command, Result<PruneOrphanVolumes.Response>> {
    public sealed record Command;
    public sealed record Response(IReadOnlyList<string> Removed, long? ReclaimedBytes);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        try {
            var volumes = await docker.ListVolumesAsync(ct);
            var containers = await docker.ListAllContainersAsync(ct);
            var inUse = VolumeReferences.BuildInUseMap(containers);

            // Orphaned = no project label AND zero references.
            var orphans = volumes
                .Where(v => !(v.Labels ?? []).ContainsKey(VolumeReferences.ComposeProjectLabel))
                .Where(v => !inUse.TryGetValue(v.Name, out var refs) || refs.Count == 0)
                .Select(v => v.Name)
                .ToList();

            if (orphans.Count == 0)
                return new Response([], 0);

            // Grab sizes once (cheap-when-wanted, single df call); null when df is unavailable.
            IReadOnlyDictionary<string, long>? sizes = null;
            try {
                sizes = await docker.GetVolumeSizesAsync(ct);
            } catch (HttpRequestException) {
                sizes = null;
            }

            var removed = new List<string>(orphans.Count);
            long reclaimed = 0;
            foreach (var name in orphans) {
                try {
                    await docker.RemoveVolumeAsync(name, ct);
                    removed.Add(name);
                    if (sizes is not null && sizes.TryGetValue(name, out var bytes))
                        reclaimed += bytes;
                } catch (HttpRequestException) {
                    // Skip a volume that raced into use; continue pruning the rest.
                }
            }

            return new Response(removed, sizes is null ? null : reclaimed);
        } catch (HttpRequestException ex) {
            return AppError.Internal($"Docker Engine API error: {ex.Message}");
        }
    }
}
