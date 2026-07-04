using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Validates the compose configuration then starts the pull + coordinator-spawn in the background.
/// Returns as soon as validation passes; the UI polls <c>system.getSelf</c> for apply progress.
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
