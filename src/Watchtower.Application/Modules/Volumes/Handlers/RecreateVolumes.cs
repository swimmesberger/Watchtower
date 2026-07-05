using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Volumes.Handlers;

/// <summary>
/// The wipe-a-database flow. Does NOT call Docker directly — it enqueues a job on the existing
/// per-stack deploy queue (triggeredBy = <c>volume-recreate</c>) so it streams to Deploy History
/// like any deploy. Server sequence inside the pipeline: compose down (keeps volumes) →
/// <c>docker volume rm</c> each selected → normal pull/up which recreates them empty.
///
/// F3 guardrail: every submitted volume name must exist AND carry the target stack's
/// <c>com.docker.compose.project</c> label. Any mismatch fails validation and nothing is enqueued.
/// </summary>
[Handler("volumes.recreate")]
public sealed class RecreateVolumes(WatchtowerDbContext db, DockerEngineClient docker, DeployQueueService deployQueue)
    : IHandler<RecreateVolumes.Command, Result<RecreateVolumes.Response>> {
    public sealed record Command(int StackId, IReadOnlyList<string> VolumeNames);
    public sealed record Response(VolumeRecreateAcceptedDto Deploy);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var stack = await db.Stacks.AsNoTracking().FirstOrDefaultAsync(s => s.Id == command.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        if (command.VolumeNames is null || command.VolumeNames.Count == 0)
            return AppError.Validation("No volumes selected for recreation.");

        // Fetch the current volumes once and index them by name for the guardrail check.
        IReadOnlyList<DockerVolumeInfo> volumes;
        try {
            volumes = await docker.ListVolumesAsync(ct);
        } catch (HttpRequestException ex) {
            return AppError.Internal($"Docker Engine API error: {ex.Message}");
        }

        var byName = volumes.ToDictionary(v => v.Name, StringComparer.Ordinal);

        // F3: validate EVERY name — must exist and belong to this stack's compose project.
        var offenders = new List<string>();
        foreach (var name in command.VolumeNames) {
            if (!byName.TryGetValue(name, out var vol)) {
                offenders.Add(name);
                continue;
            }
            var project = (vol.Labels ?? []).TryGetValue(VolumeReferences.ComposeProjectLabel, out var p) ? p : null;
            if (!string.Equals(project, stack.ComposeProjectName, StringComparison.Ordinal))
                offenders.Add(name);
        }

        if (offenders.Count > 0)
            return AppError.Validation(
                $"These volumes do not exist or do not belong to stack '{stack.Name}': {string.Join(", ", offenders)}");

        var result = deployQueue.Enqueue(command.StackId, "volume-recreate", command.VolumeNames);
        return new Response(new VolumeRecreateAcceptedDto(result.DeployEventId, result.Status));
    }
}
