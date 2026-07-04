using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Fetches the remote manifest digest of the configured image, compares it with the local image,
/// and caches the result. May take a few seconds.
/// </summary>
[Handler("system.check")]
public sealed class CheckSelfUpdate(SelfUpdateService selfUpdate)
    : IHandler<CheckSelfUpdate.Command, Result<CheckSelfUpdate.Response>> {
    public sealed record Command;
    public sealed record Response(SelfUpdateStatus Status);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        try {
            return new Response(await selfUpdate.CheckForUpdateAsync(ct));
        } catch (InvalidOperationException ex) {
            return AppError.Validation(ex.Message);
        }
    }
}
