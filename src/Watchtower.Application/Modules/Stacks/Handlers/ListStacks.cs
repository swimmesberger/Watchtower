using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>Lists all stacks (with cached update-check data) ordered by name.</summary>
[Handler("stacks.list")]
public sealed class ListStacks(WatchtowerDbContext db)
    : IHandler<ListStacks.Query, Result<ListStacks.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<StackDto> Stacks);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var stacks = await db.Stacks.AsNoTracking()
            .Include(s => s.UpdateCheck)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        var items = stacks.Select(s => StackMapping.ToDto(s, s.UpdateCheck)).ToList();
        return new Response(items);
    }
}
