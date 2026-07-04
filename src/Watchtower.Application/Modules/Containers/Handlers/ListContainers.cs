using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Containers.Handlers;

/// <summary>
/// Returns all running containers. Containers belonging to a compose stack have their stack name
/// resolved from the <c>com.docker.compose.project</c> label.
/// </summary>
[Handler("containers.list")]
public sealed class ListContainers(DockerEngineClient docker)
    : IHandler<ListContainers.Query, Result<ListContainers.Response>> {
    private const string ComposeProjectLabel = "com.docker.compose.project";

    public sealed record Query;
    public sealed record Response(IReadOnlyList<ContainerDto> Containers);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        try {
            var containers = await docker.ListContainersAsync(ct);
            var items = containers
                .Select(c => new ContainerDto(
                    c.Id, c.Names, c.Image, c.State, c.Status,
                    ExtractHealth(c.Status),
                    c.Labels.TryGetValue(ComposeProjectLabel, out var project) ? project : null))
                .ToList();
            return new Response(items);
        } catch (HttpRequestException ex) {
            return AppError.Internal($"Docker Engine API error: {ex.Message}");
        }
    }

    /// <summary>
    /// Parses the Docker status string (e.g. "Up 3 hours (unhealthy)") and returns the
    /// health state: "healthy", "unhealthy", "starting", or null when absent.
    /// </summary>
    private static string? ExtractHealth(string status) {
        if (status.Contains("(unhealthy)", StringComparison.OrdinalIgnoreCase)) return "unhealthy";
        if (status.Contains("(healthy)", StringComparison.OrdinalIgnoreCase)) return "healthy";
        if (status.Contains("(health: starting)", StringComparison.OrdinalIgnoreCase)) return "starting";
        return null;
    }
}
