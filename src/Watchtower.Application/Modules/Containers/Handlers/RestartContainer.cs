using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Containers.Handlers;

/// <summary>Restarts the specified container.</summary>
[Handler("containers.restart")]
public sealed class RestartContainer(DockerEngineClient docker)
    : IHandler<RestartContainer.Command, Result<RestartContainer.Response>> {
    public sealed record Command(string Id);
    public sealed record Response(string Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        try {
            await docker.RestartContainerAsync(command.Id, ct);
            return new Response(command.Id);
        } catch (HttpRequestException ex) {
            return AppError.Internal(ex.Message);
        }
    }
}
