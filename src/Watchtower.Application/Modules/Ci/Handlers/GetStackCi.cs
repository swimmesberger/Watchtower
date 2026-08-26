using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>
/// The CI view of the product a stack is a running copy of — a thin forward to
/// <see cref="GetProductCi"/>, kept so clients pinned to the stack-scoped call keep working while the
/// CI surface moves to the product page (ADR-0026 decision 7).
/// </summary>
/// <remarks>
/// <b>Forwarding shim, scheduled for removal.</b> CI belongs to the repository, never to one running
/// copy of it: the answer this returns is exactly <c>ci.getProductCi(stack.ProductId)</c>. Delete it
/// once no client calls it — the frontend already reads the product-scoped method.
/// </remarks>
[Handler("ci.getStackCi")]
public sealed class GetStackCi(
    WatchtowerDbContext db, IHandler<GetProductCi.Query, Result<GetProductCi.Response>> getProductCi)
    : IHandler<GetStackCi.Query, Result<GetStackCi.Response>> {
    public sealed record Query(int StackId) : IQuery;

    public sealed record Response(CiLinkDto Ci);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var productId = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == query.StackId).Select(s => (int?)s.ProductId).FirstOrDefaultAsync(ct);
        if (productId is not { } id)
            return AppError.NotFound($"Stack {query.StackId} not found.");

        var forwarded = await getProductCi.HandleAsync(new GetProductCi.Query(id), ct);
        return forwarded.IsSuccess ? new Response(forwarded.Value.Ci) : forwarded.Error;
    }
}
