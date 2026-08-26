using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// Turns the product's release webhook on or off.
/// </summary>
/// <remarks>
/// Enabling generates a token when there is none, so the two states the endpoint treats as closed —
/// disabled, and enabled-without-a-token — never have to be told apart by an operator. Disabling keeps
/// the token: re-enabling later must not invalidate the secret already sitting in somebody's CI
/// configuration, and a token nothing accepts is inert.
/// </remarks>
[Handler("products.setReleaseWebhook")]
public sealed class SetReleaseWebhook(WatchtowerDbContext db, AuditLog audit, ICurrentUser currentUser)
    : IHandler<SetReleaseWebhook.Command, Result<SetReleaseWebhook.Response>> {
    /// <summary>Audit action recorded for a toggle.</summary>
    public const string AuditAction = "release.webhook.toggle";

    public sealed record Command(int ProductId, bool Enabled);

    /// <summary>
    /// The state after the change. Deliberately without the token: enabling may have generated one, and
    /// a reader that needs the value reads it from <c>products.get</c> — the one place it is served, so
    /// there is exactly one answer to "where does the token come from".
    /// </summary>
    public sealed record Response(bool Enabled);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == command.ProductId, ct);
        if (product is null)
            return AppError.NotFound($"Product {command.ProductId} not found.");

        var wasEnabled = product.ReleaseWebhookEnabled;
        var generated = false;
        if (command.Enabled && product.ReleaseWebhookToken is null) {
            product.ReleaseWebhookToken = ReleaseWebhookTokens.Generate();
            generated = true;
        }
        product.ReleaseWebhookEnabled = command.Enabled;

        if (wasEnabled != command.Enabled || generated) {
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync(
                ProductMapping.AuditCategory, AuditAction, product.Name,
                (command.Enabled ? "enabled" : "disabled") + (generated ? "; token generated" : string.Empty),
                actor: await audit.ActorAsync(currentUser, ct), ct: ct);
        }

        return new Response(product.ReleaseWebhookEnabled);
    }
}
