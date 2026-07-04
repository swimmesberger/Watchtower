using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>Returns a single stack by id.</summary>
[Handler("stacks.get")]
public sealed class GetStack(WatchtowerDbContext db)
    : IHandler<GetStack.Query, Result<GetStack.Response>> {
    public sealed record Query(int Id);
    public sealed record Response(StackDto Stack);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var stack = await db.Stacks.AsNoTracking()
            .Include(s => s.UpdateCheck)
            .FirstOrDefaultAsync(s => s.Id == query.Id, ct);
        return stack is null
            ? AppError.NotFound($"Stack {query.Id} not found")
            : new Response(StackMapping.ToDto(stack, stack.UpdateCheck));
    }
}
