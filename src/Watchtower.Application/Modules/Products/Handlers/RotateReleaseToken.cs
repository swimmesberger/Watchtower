using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// Generates or replaces the product's release webhook token, and enables the webhook.
/// </summary>
/// <remarks>
/// Rotating always enables: an operator asks for a token because something is about to use it, and
/// handing back a token the endpoint would answer 404 for is a trap. The previous token stops working
/// the moment this returns — whatever holds it has to be updated. When the product syncs its release
/// secrets, "whatever holds it" is the repository's Actions secret, and updating it is this handler's
/// job too: it drops the sync hash and wakes the reconcile loop, so the new token is at GitHub within
/// a pass instead of after the next unrelated config change.
/// </remarks>
[Handler("products.rotateReleaseToken")]
public sealed class RotateReleaseToken(
    WatchtowerDbContext db, CiRunnerOrchestrator orchestrator, AuditLog audit, ICurrentUser currentUser)
    : IHandler<RotateReleaseToken.Command, Result<RotateReleaseToken.Response>> {
    /// <summary>Audit action recorded for a rotation.</summary>
    public const string AuditAction = "release.token.rotate";

    public sealed record Command(int ProductId);

    /// <param name="Token">The new token, in full — it is never shown again except by reading the product.</param>
    /// <param name="Resyncing">
    /// True when the product syncs its release secrets, so the caller can say "it is on its way to
    /// GitHub" rather than "go and paste it somewhere".
    /// </param>
    public sealed record Response(string Token, bool Enabled, bool Resyncing);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == command.ProductId, ct);
        if (product is null)
            return AppError.NotFound($"Product {command.ProductId} not found.");

        var first = product.ReleaseWebhookToken is null;
        product.ReleaseWebhookToken = ReleaseWebhookTokens.Generate();
        product.ReleaseWebhookEnabled = true;
        // The hash is over the token, so it no longer matches and the next pass would re-push anyway;
        // clearing it states the intent rather than relying on that, and clears any standing failure so
        // the guard's "…and no error" arm cannot hold a stale message over a fresh value.
        var resyncing = product.SyncReleaseSecrets;
        if (resyncing) {
            product.ActionsSyncedHash = null;
            product.ActionsSyncedAt = null;
            product.LastActionsSyncError = null;
        }
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(
            ProductMapping.AuditCategory, AuditAction, product.Name,
            (first ? "token generated; webhook enabled" : "token replaced; webhook enabled")
            + (resyncing ? "; queued for re-sync to GitHub Actions" : string.Empty),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        if (resyncing && product.CiRepoId is { } repoId) {
            // Save-to-retry: the operator is waiting for the new token to reach the workflow, and a
            // standing defer from an earlier failure must not add five minutes to that.
            orchestrator.ClearActionsSyncBackoff(repoId);
            orchestrator.RequestReconcile();
        }

        return new Response(product.ReleaseWebhookToken, product.ReleaseWebhookEnabled, resyncing);
    }
}
