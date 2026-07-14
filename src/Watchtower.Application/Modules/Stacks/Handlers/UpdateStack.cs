using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>Updates a stack definition. When <c>EnvVars</c> is provided it atomically replaces all env vars.</summary>
[Handler("stacks.update")]
public sealed class UpdateStack(WatchtowerDbContext db)
    : IHandler<UpdateStack.Command, Result<UpdateStack.Response>> {
    public sealed record Command(
        int Id,
        string Name,
        string RepositoryUrl,
        string ComposeFilePath,
        string Branch,
        string? ComposeProjectName,
        int? CredentialId,
        string? WebhookToken,
        bool WebhookEnabled,
        string? AutoDeployMode,
        string? AutoDeployTime,
        IReadOnlyList<StackEnvVarInput>? EnvVars);

    public sealed record Response(StackDto Stack);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.Id == command.Id, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.Id} not found");

        if (command.EnvVars is not null && StackMapping.FirstDuplicateKey(command.EnvVars) is { } dup)
            return AppError.Validation($"Duplicate env var key: '{dup}'");

        if (StackMapping.ParseMode(command.AutoDeployMode) is not { } autoDeployMode)
            return AppError.Validation($"Invalid auto-deploy mode: '{command.AutoDeployMode}'");
        var autoDeployTime = command.AutoDeployTime;
        if (StackMapping.ValidateAutoDeploy(autoDeployMode, ref autoDeployTime) is { } autoDeployError)
            return AppError.Validation(autoDeployError);

        stack.Name = command.Name;
        stack.RepositoryUrl = command.RepositoryUrl;
        stack.ComposeFilePath = command.ComposeFilePath;
        stack.Branch = command.Branch;
        stack.ComposeProjectName = StackMapping.ResolveProjectName(command.Name, command.ComposeProjectName);
        stack.CredentialId = command.CredentialId;
        stack.WebhookToken = command.WebhookToken;
        stack.WebhookEnabled = command.WebhookEnabled;
        stack.AutoDeployMode = autoDeployMode;
        stack.AutoDeployTime = autoDeployTime;

        if (command.EnvVars is not null) {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.StackEnvVars.Where(v => v.StackId == stack.Id).ExecuteDeleteAsync(ct);
            foreach (var v in command.EnvVars)
                db.StackEnvVars.Add(new StackEnvVar { StackId = stack.Id, Key = v.Key, Value = v.Value });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        } else {
            await db.SaveChangesAsync(ct);
        }

        var check = await db.StackUpdateChecks.AsNoTracking().FirstOrDefaultAsync(c => c.StackId == stack.Id, ct);
        return new Response(StackMapping.ToDto(stack, check));
    }
}
