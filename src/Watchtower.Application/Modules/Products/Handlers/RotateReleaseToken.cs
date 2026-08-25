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
/// the moment this returns — whatever holds it (a repository secret pasted by hand until secret sync
/// lands) has to be updated.
/// </remarks>
[Handler("products.rotateReleaseToken")]
public sealed class RotateReleaseToken(WatchtowerDbContext db, AuditLog audit, ICurrentUser currentUser)
    : IHandler<RotateReleaseToken.Command, Result<RotateReleaseToken.Response>> {
    /// <summary>Audit action recorded for a rotation.</summary>
    public const string AuditAction = "release.token.rotate";

    public sealed record Command(int ProductId);

    /// <param name="Token">The new token, in full — it is never shown again except by reading the product.</param>
    public sealed record Response(string Token, bool Enabled);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == command.ProductId, ct);
        if (product is null)
            return AppError.NotFound($"Product {command.ProductId} not found.");

        var first = product.ReleaseWebhookToken is null;
        product.ReleaseWebhookToken = ReleaseWebhookTokens.Generate();
        product.ReleaseWebhookEnabled = true;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(
            ProductMapping.AuditCategory, AuditAction, product.Name,
            first ? "token generated; webhook enabled" : "token replaced; webhook enabled",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(product.ReleaseWebhookToken, product.ReleaseWebhookEnabled);
    }
}
