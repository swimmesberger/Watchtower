using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Returns Watchtower's self-update configuration and the cached result of the most recent check.
/// Inspects the running container to provide auto-detected image/compose values.
/// </summary>
[Handler("system.getSelf")]
public sealed class GetSelf(SelfUpdateService selfUpdate)
    : IHandler<GetSelf.Query, Result<GetSelf.Response>> {
    public sealed record Query;
    public sealed record Response(SelfUpdateStatus Status);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) =>
        new Response(await selfUpdate.GetStatusAsync(ct));
}
