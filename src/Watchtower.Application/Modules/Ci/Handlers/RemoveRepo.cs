using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>
/// Removes a CI repo. Its runner containers become orphans that the next reconcile pass stops,
/// deregisters (best effort), and removes. The credential and cache volumes are left in place.
/// </summary>
/// <remarks>
/// Removing the repo also turns off release-secret sync for every product that was syncing into it,
/// because the PAT that did the pushing left with the repo. Without that clear the products keep
/// <c>SyncReleaseSecrets = true</c> while the <c>SET NULL</c> foreign key drops their
/// <c>ci_repo_id</c> — and a null FK is outside the filtered unique index's reach (PostgreSQL treats
/// NULLs as distinct), so a later "enable sync for a different product of the same repository" would
/// be accepted and the two would fight over one fixed set of secret names. The UI would meanwhile
/// still read the surviving hash and claim the product was synced. Clearing is the fix at the source;
/// the sync pass refusing an ambiguous repo is the belt to this pair of braces.
/// </remarks>
[Handler("ci.removeRepo")]
public sealed class RemoveRepo(
    WatchtowerDbContext db,
    CiRepoResolver resolver,
    CiRunnerOrchestrator orchestrator,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<RemoveRepo.Command, Result<RemoveRepo.Response>> {
    /// <summary>Audit action recorded when removal turns a product's release-secret sync off.</summary>
    public const string SyncClearedAction = "release-token.sync.cleared";

    public sealed record Command(int Id);

    public sealed record Response(bool Removed);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var repo = await db.CiRepos.FirstOrDefaultAsync(r => r.Id == command.Id, ct);
        if (repo is null)
            return AppError.NotFound($"CI repo {command.Id} not found.");

        // Collected before the delete: afterwards the FK is null and the URL is the only thing left
        // pointing here, which is precisely the state this exists to avoid creating.
        var syncing = await resolver.FindSyncingProductsAsync(repo, ct);
        foreach (var product in syncing) {
            product.SyncReleaseSecrets = false;
            product.ActionsSyncedHash = null;
            product.ActionsSyncedAt = null;
            product.LastActionsSyncError = null;
        }

        db.CiRepos.Remove(repo);
        await db.SaveChangesAsync(ct);

        var actor = await audit.ActorAsync(currentUser, ct);
        await audit.RecordAsync("ci", "repo.remove", repo.FullName,
            "removed CI runners (runner containers reaped on the next reconcile pass; "
            + "cache volumes and synced GitHub values left in place)"
            + (syncing.Count > 0
                ? $"; release secret sync turned off for {string.Join(", ", syncing.Select(p => $"'{p.Name}'"))}"
                : string.Empty),
            actor: actor, ct: ct);
        // Its own row per product as well as the line above: "when did this product stop syncing, and
        // why" is a question asked from the product's side, and it should not need the reader to know
        // that a CI repo was removed that day.
        foreach (var product in syncing) {
            await audit.RecordAsync("ci", SyncClearedAction, repo.FullName,
                $"release secret sync turned off for product '{product.Name}' because CI was removed for "
                + $"{repo.FullName}; the values already at GitHub are left in place",
                actor: actor, ct: ct);
        }

        orchestrator.RequestReconcile();
        return new Response(true);
    }
}
