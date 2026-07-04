using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>
/// Atomically replaces all environment variables for a stack. Pass an empty list to clear them.
/// Duplicate keys in the request are rejected.
/// </summary>
[Handler("stacks.setEnv")]
public sealed class SetStackEnv(WatchtowerDbContext db)
    : IHandler<SetStackEnv.Command, Result<SetStackEnv.Response>> {
    public sealed record Command(int StackId, IReadOnlyList<StackEnvVarInput> Vars);
    public sealed record Response(IReadOnlyList<StackEnvVarDto> EnvVars);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (!await db.Stacks.AnyAsync(s => s.Id == command.StackId, ct))
            return AppError.NotFound($"Stack {command.StackId} not found");

        if (StackMapping.FirstDuplicateKey(command.Vars) is { } dup)
            return AppError.Validation($"Duplicate key: '{dup}'");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.StackEnvVars.Where(v => v.StackId == command.StackId).ExecuteDeleteAsync(ct);
        foreach (var v in command.Vars)
            db.StackEnvVars.Add(new StackEnvVar { StackId = command.StackId, Key = v.Key, Value = v.Value });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var saved = await db.StackEnvVars.AsNoTracking()
            .Where(v => v.StackId == command.StackId)
            .OrderBy(v => v.Key)
            .Select(v => new StackEnvVarDto(v.Id, v.Key, v.Value))
            .ToListAsync(ct);
        return new Response(saved);
    }
}
