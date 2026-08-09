using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>Lists configured CI repos with the orchestrator's live runner status merged in.</summary>
[Handler("ci.listRepos")]
public sealed class ListRepos(WatchtowerDbContext db, CiRunnerOrchestrator orchestrator)
    : IHandler<ListRepos.Query, Result<ListRepos.Response>> {
    public sealed record Query : IQuery;

    public sealed record Response(IReadOnlyList<CiRepoDto> Repos);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var repos = await db.CiRepos.AsNoTracking().OrderBy(r => r.Owner).ThenBy(r => r.Name).ToListAsync(ct);
        var status = orchestrator.Status;
        return new Response(repos
            .Select(r => CiMapping.ToDto(r, status.TryGetValue(r.Id, out var s) ? s : null))
            .ToList());
    }
}
