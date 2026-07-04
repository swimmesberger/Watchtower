using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>Returns all environment variable overrides defined for a stack.</summary>
[Handler("stacks.getEnv")]
public sealed class GetStackEnv(WatchtowerDbContext db)
    : IHandler<GetStackEnv.Query, Result<GetStackEnv.Response>> {
    public sealed record Query(int StackId);
    public sealed record Response(IReadOnlyList<StackEnvVarDto> EnvVars);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        if (!await db.Stacks.AnyAsync(s => s.Id == query.StackId, ct))
            return AppError.NotFound($"Stack {query.StackId} not found");

        var vars = await db.StackEnvVars.AsNoTracking()
            .Where(v => v.StackId == query.StackId)
            .OrderBy(v => v.Key)
            .Select(v => new StackEnvVarDto(v.Id, v.Key, v.Value))
            .ToListAsync(ct);
        return new Response(vars);
    }
}
