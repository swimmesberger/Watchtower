using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>Live runner-slot state for one configured repo (poll target for the UI).</summary>
[Handler("ci.getRunnerStatus")]
public sealed class GetRunnerStatus(WatchtowerDbContext db, CiRunnerOrchestrator orchestrator)
    : IHandler<GetRunnerStatus.Query, Result<GetRunnerStatus.Response>> {
    public sealed record Query(int RepoId) : IQuery;

    public sealed record Response(CiRunnerStatusDto? Status);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var exists = await db.CiRepos.AsNoTracking().AnyAsync(r => r.Id == query.RepoId, ct);
        if (!exists)
            return AppError.NotFound($"CI repo {query.RepoId} not found.");

        var status = orchestrator.Status.TryGetValue(query.RepoId, out var s) ? CiMapping.ToDto(s) : null;
        return new Response(status);
    }
}
