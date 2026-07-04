using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>Creates a new stack. Initial environment variables (if any) are set atomically.</summary>
[Handler("stacks.create")]
public sealed class CreateStack(WatchtowerDbContext db)
    : IHandler<CreateStack.Command, Result<CreateStack.Response>> {
    public sealed record Command(
        string Name,
        string RepositoryUrl,
        string ComposeFilePath,
        string Branch,
        string? ComposeProjectName,
        int? CredentialId,
        string? WebhookToken,
        bool WebhookEnabled,
        IReadOnlyList<StackEnvVarInput>? EnvVars);

    public sealed record Response(StackDto Stack);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (command.EnvVars is { Count: > 0 } && StackMapping.FirstDuplicateKey(command.EnvVars) is { } dup)
            return AppError.Validation($"Duplicate env var key: '{dup}'");

        var stack = new Stack {
            Name = command.Name,
            RepositoryUrl = command.RepositoryUrl,
            ComposeFilePath = command.ComposeFilePath,
            Branch = command.Branch,
            ComposeProjectName = StackMapping.ResolveProjectName(command.Name, command.ComposeProjectName),
            CredentialId = command.CredentialId,
            WebhookToken = command.WebhookToken,
            WebhookEnabled = command.WebhookEnabled,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(ct);

        if (command.EnvVars is { Count: > 0 }) {
            foreach (var v in command.EnvVars)
                db.StackEnvVars.Add(new StackEnvVar { StackId = stack.Id, Key = v.Key, Value = v.Value });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);

        return new Response(StackMapping.ToDto(stack, check: null));
    }
}
