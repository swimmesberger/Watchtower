using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Verifies Watchtower is running as a container and that no apply is already in flight, then starts
/// the image pull + coordinator-spawn in the background — the coordinator recreates the container
/// through the Docker API, no compose file is involved.
/// Returns as soon as those checks pass; the UI polls <c>system.getSelf</c> for apply progress.
/// </summary>
[Handler("system.applyUpdate")]
public sealed class ApplySelfUpdate(SelfUpdateService selfUpdate)
    : IHandler<ApplySelfUpdate.Command, Result<ApplySelfUpdate.Response>> {
    public sealed record Command;
    public sealed record Response(bool Accepted);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        try {
            await selfUpdate.ApplyUpdateAsync(ct);
            return new Response(true);
        } catch (InvalidOperationException ex) {
            return AppError.Validation(ex.Message);
        }
    }
}
