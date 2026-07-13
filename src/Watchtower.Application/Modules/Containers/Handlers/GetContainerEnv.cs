using System.Net;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Containers.Handlers;

/// <summary>
/// Returns the environment variables a container is actually running with, as reported by the
/// Docker inspect API (image ENV + compose interpolation applied — not the configured overrides).
/// </summary>
[Handler("containers.env")]
public sealed class GetContainerEnv(DockerEngineClient docker)
    : IHandler<GetContainerEnv.Query, Result<GetContainerEnv.Response>> {
    public sealed record Query(string Id);
    public sealed record Response(IReadOnlyList<ContainerEnvVarDto> EnvVars);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        try {
            var details = await docker.InspectContainerAsync(query.Id, ct);
            var vars = (details.Config.Env ?? [])
                .Select(ParseEnvEntry)
                .OrderBy(v => v.Key, StringComparer.Ordinal)
                .ToList();
            return new Response(vars);
        } catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) {
            return AppError.NotFound($"Container {query.Id} not found");
        } catch (HttpRequestException ex) {
            return AppError.Internal($"Docker Engine API error: {ex.Message}");
        }
    }

    /// <summary>Splits at the first '=' only — the value itself may contain '='.</summary>
    private static ContainerEnvVarDto ParseEnvEntry(string entry) {
        var separator = entry.IndexOf('=');
        return separator < 0
            ? new ContainerEnvVarDto(entry, "")
            : new ContainerEnvVarDto(entry[..separator], entry[(separator + 1)..]);
    }
}
