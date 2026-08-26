using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>
/// Enables CI runners for the product a stack is a running copy of — a thin forward to
/// <see cref="EnableForProduct"/>, kept so clients pinned to the stack-scoped call keep working while
/// the CI surface moves to the product page (ADR-0026 decision 7).
/// </summary>
/// <remarks>
/// <b>Forwarding shim, scheduled for removal.</b> It resolves the stack's product and calls
/// <c>ci.enableForProduct</c>; the credential default, the up-front PAT probe and the audit row all come
/// from there, so its messages name the product rather than the stack. Delete it once no client calls
/// it — the frontend already uses the product-scoped method.
/// </remarks>
[Handler("ci.enableForStack")]
public sealed class EnableForStack(
    WatchtowerDbContext db,
    IHandler<EnableForProduct.Command, Result<EnableForProduct.Response>> enableForProduct)
    : IHandler<EnableForStack.Command, Result<EnableForStack.Response>> {
    /// <param name="CredentialId">
    /// Credential holding the runner-admin PAT. Null uses the product's clone credential.
    /// </param>
    public sealed record Command(int StackId, int? CredentialId = null);

    public sealed record Response(CiRepoDto Repo);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var productId = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == command.StackId).Select(s => (int?)s.ProductId).FirstOrDefaultAsync(ct);
        if (productId is not { } id)
            return AppError.NotFound($"Stack {command.StackId} not found.");

        var forwarded = await enableForProduct.HandleAsync(
            new EnableForProduct.Command(id, command.CredentialId), ct);
        return forwarded.IsSuccess ? new Response(forwarded.Value.Repo) : forwarded.Error;
    }
}
