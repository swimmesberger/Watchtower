using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>Returns a single stack by id.</summary>
[Handler("stacks.get")]
public sealed class GetStack(WatchtowerDbContext db, StackUpdateRevalidator revalidator)
    : IHandler<GetStack.Query, Result<GetStack.Response>> {
    public sealed record Query(int Id);
    public sealed record Response(StackDto Stack);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var stack = await db.Stacks.AsNoTracking()
            .Include(s => s.UpdateCheck)
            // The source lives on the product since ADR-0026, and the branch may be overridden by the
            // tenant's template, so both navigations are what makes the DTO's source fields resolvable.
            .Include(s => s.Product)
            .Include(s => s.Template)
            .FirstOrDefaultAsync(s => s.Id == query.Id, ct);
        if (stack is null) return AppError.NotFound($"Stack {query.Id} not found");
        // A pending image update may already have been applied on the host by hand; revalidate that
        // in the background (no registry traffic, never awaited) so the next refetch is accurate.
        // Rows with no recorded digests predate that column: only a full check can correct those.
        if (stack.UpdateCheck is { HasUpdates: true, OutdatedImages.Length: > 0, OutdatedImageDigests.Count: > 0 })
            revalidator.Request(stack.Id);
        return new Response(StackMapping.ToDto(stack, stack.UpdateCheck));
    }
}
