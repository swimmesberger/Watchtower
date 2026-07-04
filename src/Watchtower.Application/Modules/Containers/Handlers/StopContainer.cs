using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Containers.Handlers;

/// <summary>Stops the specified container.</summary>
[Handler("containers.stop")]
public sealed class StopContainer(DockerEngineClient docker)
    : IHandler<StopContainer.Command, Result<StopContainer.Response>> {
    public sealed record Command(string Id);
    public sealed record Response(string Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        try {
            await docker.StopContainerAsync(command.Id, ct);
            return new Response(command.Id);
        } catch (HttpRequestException ex) {
            return AppError.Internal(ex.Message);
        }
    }
}
