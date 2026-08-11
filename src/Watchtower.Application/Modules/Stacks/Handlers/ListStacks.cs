using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>Lists all stacks (with cached update-check data) ordered by name.</summary>
[Handler("stacks.list")]
public sealed class ListStacks(WatchtowerDbContext db, StackUpdateRevalidator revalidator)
    : IHandler<ListStacks.Query, Result<ListStacks.Response>> {
    public sealed record Query;
    public sealed record Response(IReadOnlyList<StackDto> Stacks);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var stacks = await db.Stacks.AsNoTracking()
            .Include(s => s.UpdateCheck)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);
        var items = stacks.Select(s => StackMapping.ToDto(s, s.UpdateCheck)).ToList();
        // The values above are what the last check saw; anything an operator updated by hand since is
        // corrected in the background and shows up on the next refetch. Never awaited.
        foreach (var stack in stacks)
            if (stack.UpdateCheck is { HasUpdates: true, OutdatedImages.Length: > 0 })
                revalidator.Request(stack.Id);
        return new Response(items);
    }
}
