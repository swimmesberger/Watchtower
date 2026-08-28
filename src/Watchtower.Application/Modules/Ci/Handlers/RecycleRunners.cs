using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>
/// Recycles a repo's whole runner pool on operator request — the "recreate all now" counterpart to
/// the automatic idle-only recycle on settings changes. Idle runners are deregistered at GitHub and
/// removed; the woken reconcile loop refills the pool under the current settings. Runners executing
/// a job are kept and counted, unless <c>Force</c> removes them too (failing their jobs).
/// </summary>
[Handler("ci.recycleRunners")]
public sealed class RecycleRunners(
    WatchtowerDbContext db,
    CiRunnerOrchestrator orchestrator,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<RecycleRunners.Command, Result<RecycleRunners.Response>> {
    public sealed record Command(int RepoId, bool Force = false);

    /// <param name="Recycled">Containers removed; the reconcile loop replaces each of them.</param>
    /// <param name="Busy">
    /// Containers kept because their runner is executing a job. Retry with <c>Force</c> to remove
    /// them anyway, failing those jobs.
    /// </param>
    public sealed record Response(int Recycled, int Busy);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var repo = await db.CiRepos.AsNoTracking().Include(r => r.Credential)
            .FirstOrDefaultAsync(r => r.Id == command.RepoId, ct);
        if (repo is null)
            return AppError.NotFound($"CI repo {command.RepoId} not found.");

        var outcome = await orchestrator.RecycleRunnersAsync(repo, containerId: null, command.Force, ct);

        if (outcome.Recycled > 0) {
            await audit.RecordAsync("ci", "runner.recycle", repo.FullName,
                $"{outcome.Recycled} runner container(s) recycled"
                + (outcome.Busy > 0 ? $", {outcome.Busy} busy kept" : "")
                + (command.Force ? " (forced)" : ""),
                actor: await audit.ActorAsync(currentUser, ct), ct: ct);
        }
        return new Response(outcome.Recycled, outcome.Busy);
    }
}
