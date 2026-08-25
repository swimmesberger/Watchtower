using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>Creates a new stack. Initial environment variables (if any) are set atomically.</summary>
/// <remarks>
/// ADR-0026's implicit-product contract lives here: the inline repository fields stay, and the product
/// behind them is found-or-created silently, so creating a stack remains one form and every existing
/// API client keeps working. A caller that already knows its product passes <c>ProductId</c> instead —
/// supplying both is a validation error rather than a silent precedence rule nobody could guess.
/// </remarks>
[Handler("stacks.create")]
public sealed class CreateStack(
    WatchtowerDbContext db,
    SelfProjectNameProvider selfProjects,
    ProductCatalog products,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<CreateStack.Command, Result<CreateStack.Response>> {
    /// <param name="ProductId">
    /// An existing product to deploy. When set, the repository fields must be absent or empty, and
    /// <paramref name="Branch"/> becomes a per-stack override if it differs from the product default.
    /// </param>
    public sealed record Command(
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
        IReadOnlyList<StackEnvVarInput>? EnvVars,
        int? ProductId = null);

    public sealed record Response(StackDto Stack);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (command.EnvVars is { Count: > 0 } && StackMapping.FirstDuplicateKey(command.EnvVars) is { } dup)
            return AppError.Validation($"Duplicate env var key: '{dup}'");

        if (StackMapping.ParseMode(command.AutoDeployMode) is not { } autoDeployMode)
            return AppError.Validation($"Invalid auto-deploy mode: '{command.AutoDeployMode}'");
        var autoDeployTime = command.AutoDeployTime;
        if (StackMapping.ValidateAutoDeploy(autoDeployMode, ref autoDeployTime) is { } autoDeployError)
            return AppError.Validation(autoDeployError);

        // Two stacks sharing a compose project name would share containers — and with them App API
        // visibility. Enforced here because the default name is the lowercased stack name. Watchtower's
        // own project is reserved for the same reason.
        var projectName = StackMapping.ResolveProjectName(command.Name, command.ComposeProjectName);
        if (await StackProjectNames.ValidateAsync(db, selfProjects, projectName, excludeStackId: null, ct)
            is { } projectNameError)
            return AppError.Validation(projectNameError);

        // Opened before the product is resolved so a product created implicitly for this stack rolls
        // back with it: a failed creation must not leave an orphan in the catalogue.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var (product, created, productError) = await products.ResolveAsync(
            command.ProductId, command.RepositoryUrl, command.ComposeFilePath, command.Branch,
            command.CredentialId, ct);
        if (productError is { } error) return error;

        var stack = new Stack {
            Name = command.Name,
            ProductId = product!.Id,
            Product = product,
            // The product default is the right base here, unlike on stacks.update: a stack created
            // through this handler has no template, so there is no inherited override to compare against.
            BranchOverride = ProductSourceResolver.OverrideFor(command.Branch, product.DefaultBranch),
            ComposeProjectName = projectName,
            WebhookToken = command.WebhookToken,
            WebhookEnabled = command.WebhookEnabled,
            // App API token is minted up front so operators can hand it to the application before
            // its first deploy; the deploy path only generates lazily for pre-existing stacks.
            AppApiToken = AppApiTokens.Generate(),
            AppApiEnabled = true,
            AutoDeployMode = autoDeployMode,
            AutoDeployTime = autoDeployTime,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        db.Stacks.Add(stack);
        await db.SaveChangesAsync(ct);

        if (command.EnvVars is { Count: > 0 }) {
            foreach (var v in command.EnvVars)
                db.StackEnvVars.Add(new StackEnvVar { StackId = stack.Id, Key = v.Key, Value = v.Value });
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);

        // A product that appeared because someone created a stack is still a product an operator will
        // later find in the catalogue and wonder about; the trail says where it came from.
        if (created) {
            await audit.RecordAsync(
                ProductCatalog.AuditCategory, "product.create", product.Name,
                $"{product.RepositoryUrl} ({product.ComposeFilePath}) @ {product.DefaultBranch} — "
                + "implicit via stacks.create",
                actor: await audit.ActorAsync(currentUser, ct), ct: ct);
        }

        return new Response(StackMapping.ToDto(stack, check: null));
    }
}
