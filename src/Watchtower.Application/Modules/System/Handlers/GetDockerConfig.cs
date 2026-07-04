namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Checks whether a Docker CLI config file is accessible from inside the container. Probes, in order:
/// the <c>WATCHTOWER_DOCKER_CONFIG</c> directory, the <c>DOCKER_CONFIG</c> directory, then <c>~/.docker</c>.
/// </summary>
[Handler("system.dockerConfig")]
public sealed class GetDockerConfig
    : IHandler<GetDockerConfig.Query, Result<GetDockerConfig.Response>> {
    public sealed record Query;
    public sealed record Response(DockerConfigStatus Config);

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var (path, source) = Resolve();
        var status = new DockerConfigStatus(File.Exists(path), path, source);
        return ValueTask.FromResult<Result<Response>>(new Response(status));
    }

    private static (string Path, string Source) Resolve() {
        var watchtowerDir = Environment.GetEnvironmentVariable("WATCHTOWER_DOCKER_CONFIG");
        if (!string.IsNullOrWhiteSpace(watchtowerDir))
            return (Path.Combine(watchtowerDir, "config.json"), "WATCHTOWER_DOCKER_CONFIG");

        var dockerDir = Environment.GetEnvironmentVariable("DOCKER_CONFIG");
        if (!string.IsNullOrWhiteSpace(dockerDir))
            return (Path.Combine(dockerDir, "config.json"), "DOCKER_CONFIG");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return (Path.Combine(home, ".docker", "config.json"), "default");
    }
}
