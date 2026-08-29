using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>
/// The compose service names of a stack, read from its containers' <c>com.docker.compose.service</c>
/// labels — what the device and GPU editors offer instead of asking an operator to type a service
/// name from memory (ADR-0030/0031).
/// </summary>
/// <remarks>
/// Deliberately the <em>deployed</em> services rather than the repository's compose file: the file
/// would have to be cloned and resolved per keystroke, while the labels are one cheap Docker call.
/// The consequence is that a never-deployed stack reports nothing, which the UI renders as a
/// free-text fallback — the setting has to be configurable before the first deploy, and a service
/// the engine cannot see is exactly the case the deploy already warns about.
/// <para>
/// All states, so a stopped stack still lists its services (<see cref="StopStack"/> keeps
/// containers). A Docker outage is an empty list, not an error: this is an input aid, and failing it
/// would take the whole Settings tab down with it.
/// </para>
/// </remarks>
[Handler("stacks.services")]
public sealed class ListStackServices(WatchtowerDbContext db, DockerEngineClient docker)
    : IHandler<ListStackServices.Query, Result<ListStackServices.Response>> {
    public sealed record Query(int StackId);
    /// <param name="Services">Distinct service names, ordered; empty when the stack has no containers.</param>
    public sealed record Response(IReadOnlyList<string> Services);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var projectName = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == query.StackId)
            .Select(s => s.ComposeProjectName)
            .FirstOrDefaultAsync(ct);
        if (projectName is null)
            return AppError.NotFound($"Stack {query.StackId} not found");

        IReadOnlyList<DockerContainerInfo> containers;
        try {
            containers = await docker.ListContainersByLabelsAsync(
                [$"{AppApiService.ProjectLabel}={projectName}"], ct);
        } catch (HttpRequestException) {
            return new Response([]);
        }

        return new Response([.. containers
            .Select(c => c.Labels.GetValueOrDefault(AppApiService.ServiceLabel))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)]);
    }
}
