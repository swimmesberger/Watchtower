using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Volumes.Handlers;

/// <summary>
/// Deletes a single volume directly (used only for orphans). Predicts Docker's 409 by rejecting
/// with <see cref="AppError.Conflict"/> when any container (running or stopped) still references the
/// volume, BEFORE calling Docker. If a container attaches between the check and the delete, Docker's
/// own 409 message is passed through.
/// </summary>
[Handler("volumes.remove")]
public sealed class RemoveVolume(DockerEngineClient docker)
    : IHandler<RemoveVolume.Command, Result<RemoveVolume.Response>> {
    public sealed record Command(string Name);
    public sealed record Response(bool Removed);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        try {
            // Predict the 409: if any container references it, refuse before touching Docker.
            var containers = await docker.ListAllContainersAsync(ct);
            var inUse = VolumeReferences.BuildInUseMap(containers);
            if (inUse.TryGetValue(command.Name, out var refs) && refs.Count > 0)
                return AppError.Conflict($"Volume is still referenced by a container ({string.Join(", ", refs)}).");

            await docker.RemoveVolumeAsync(command.Name, ct);
            return new Response(true);
        } catch (HttpRequestException ex) {
            // A race (container attached after the check) surfaces here as Docker's 409.
            return AppError.Conflict($"Docker refused to remove volume '{command.Name}': {ex.Message}");
        }
    }
}
