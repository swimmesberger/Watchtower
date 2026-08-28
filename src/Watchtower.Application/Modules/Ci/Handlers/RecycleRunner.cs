using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>
/// Recycles one runner container on operator request: deregister at GitHub, remove the container,
/// and wake the reconcile loop, which spawns a fresh replacement under the current settings.
/// GitHub refuses to deregister a runner mid-job, so a busy runner is left alone and reported as
/// such — unless <c>Force</c> is set, which removes the container anyway and fails the job it was
/// executing.
/// </summary>
[Handler("ci.recycleRunner")]
public sealed class RecycleRunner(
    WatchtowerDbContext db,
    CiRunnerOrchestrator orchestrator,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<RecycleRunner.Command, Result<RecycleRunner.Response>> {
    /// <param name="ContainerId">
    /// The container's id as the runner table shows it (the 12-char short form; longer prefixes of
    /// the full id work too).
    /// </param>
    public sealed record Command(int RepoId, string ContainerId, bool Force = false);

    /// <param name="Recycled">The container was removed; the reconcile loop replaces it.</param>
    /// <param name="Busy">
    /// The container was kept because its runner is executing a job. Retry with <c>Force</c> to
    /// remove it anyway, failing that job.
    /// </param>
    public sealed record Response(bool Recycled, bool Busy);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var containerId = command.ContainerId.Trim();
        // Shorter prefixes could silently resolve to a different runner of the same repo.
        if (containerId.Length < 12)
            return AppError.Validation("ContainerId must be at least the 12-character short id.");

        var repo = await db.CiRepos.AsNoTracking().Include(r => r.Credential)
            .FirstOrDefaultAsync(r => r.Id == command.RepoId, ct);
        if (repo is null)
            return AppError.NotFound($"CI repo {command.RepoId} not found.");

        var outcome = await orchestrator.RecycleRunnersAsync(repo, containerId, command.Force, ct);
        if (!outcome.Found)
            return AppError.NotFound(
                $"No runner container '{containerId}' found for {repo.FullName} — it may already be gone.");

        if (outcome.Recycled > 0) {
            await audit.RecordAsync("ci", "runner.recycle", repo.FullName,
                $"runner container {containerId} recycled{(command.Force ? " (forced)" : "")}",
                actor: await audit.ActorAsync(currentUser, ct), ct: ct);
        }
        return new Response(outcome.Recycled > 0, outcome.Busy > 0);
    }
}
