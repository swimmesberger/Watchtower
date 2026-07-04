using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Containers.Handlers;

/// <summary>Removes the specified container. It must be stopped first.</summary>
[Handler("containers.remove")]
public sealed class RemoveContainer(DockerEngineClient docker)
    : IHandler<RemoveContainer.Command, Result<RemoveContainer.Response>> {
    public sealed record Command(string Id);
    public sealed record Response(string Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        try {
            await docker.RemoveContainerAsync(command.Id, ct);
            return new Response(command.Id);
        } catch (HttpRequestException ex) {
            return AppError.Internal(ex.Message);
        }
    }
}
