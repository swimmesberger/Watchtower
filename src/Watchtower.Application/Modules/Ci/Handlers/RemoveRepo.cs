using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>
/// Removes a CI repo. Its runner containers become orphans that the next reconcile pass stops,
/// deregisters (best effort), and removes. The credential and cache volumes are left in place.
/// </summary>
[Handler("ci.removeRepo")]
public sealed class RemoveRepo(WatchtowerDbContext db, CiRunnerOrchestrator orchestrator)
    : IHandler<RemoveRepo.Command, Result<RemoveRepo.Response>> {
    public sealed record Command(int Id);

    public sealed record Response(bool Removed);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var repo = await db.CiRepos.FirstOrDefaultAsync(r => r.Id == command.Id, ct);
        if (repo is null)
            return AppError.NotFound($"CI repo {command.Id} not found.");

        db.CiRepos.Remove(repo);
        await db.SaveChangesAsync(ct);

        orchestrator.RequestReconcile();
        return new Response(true);
    }
}
