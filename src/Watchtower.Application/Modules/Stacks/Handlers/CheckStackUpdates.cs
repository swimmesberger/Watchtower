using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>Triggers an on-demand image update check for a single stack and returns the refreshed stack.</summary>
[Handler("stacks.checkUpdates")]
public sealed class CheckStackUpdates(WatchtowerDbContext db, StackUpdateService stackUpdate)
    : IHandler<CheckStackUpdates.Command, Result<CheckStackUpdates.Response>> {
    public sealed record Command(int Id);
    public sealed record Response(StackDto Stack);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var stack = await db.Stacks.AsNoTracking().FirstOrDefaultAsync(s => s.Id == command.Id, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.Id} not found");

        var result = await stackUpdate.CheckStackAsync(stack, ct);
        var check = new StackUpdateCheck {
            StackId = result.StackId,
            HasUpdates = result.HasUpdates,
            OutdatedImages = result.OutdatedImages,
            CheckedAt = result.CheckedAt,
        };
        return new Response(StackMapping.ToDto(stack, check));
    }
}
