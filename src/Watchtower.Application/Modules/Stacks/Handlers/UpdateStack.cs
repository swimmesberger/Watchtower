using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>Updates a stack definition. When <c>EnvVars</c> is provided it atomically replaces all env vars.</summary>
/// <remarks>
/// The repository fields stay in the contract but are no longer stored on the stack (ADR-0026). They
/// are compared against the stack's <em>effective</em> source instead: an unchanged value passes
/// silently — the UI posts the whole object back, so presence-based rejection would break every save —
/// and a changed one is refused with a pointer at <c>products.update</c>, which is where the source
/// lives now and where a change would reach every stack of the product.
/// <para>
/// <c>Branch</c> is the exception: it maps onto <see cref="Stack.BranchOverride"/>, so this is still
/// where a single stack is moved onto another branch of the same product. Passing the branch the stack
/// already <em>inherits</em> (or nothing) clears the override rather than pinning it — and inherits
/// means <see cref="ProductSourceResolver.InheritedBranch"/>, the template's override before the
/// product default. Comparing against the product default alone would write <c>develop</c> onto every
/// tenant of a <c>develop</c> template the first time its settings were saved, which is the
/// copy-instead-of-inherit bug ADR-0026 removes.
/// </para>
/// </remarks>
[Handler("stacks.update")]
public sealed class UpdateStack(WatchtowerDbContext db, SelfProjectNameProvider selfProjects)
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
        var stack = await db.Stacks
            .Include(s => s.Product)
            .Include(s => s.Template)
            // Invariant 6: no surface may render a Deploy button without the version it would deploy.
            .Include(s => s.PinnedRelease)
            .Include(s => s.LastDeployedRelease)
            .FirstOrDefaultAsync(s => s.Id == command.Id, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.Id} not found");

        if (command.EnvVars is not null && StackMapping.FirstDuplicateKey(command.EnvVars) is { } dup)
            return AppError.Validation($"Duplicate env var key: '{dup}'");

        if (StackMapping.ParseMode(command.AutoDeployMode) is not { } autoDeployMode)
            return AppError.Validation($"Invalid auto-deploy mode: '{command.AutoDeployMode}'");
        var autoDeployTime = command.AutoDeployTime;
        if (StackMapping.ValidateAutoDeploy(autoDeployMode, ref autoDeployTime) is { } autoDeployError)
            return AppError.Validation(autoDeployError);

        var product = stack.Product!;
        var effective = ProductSourceResolver.Resolve(stack);
        if (RefuseSourceChange(command, product, effective) is { } sourceError)
            return AppError.Validation(sourceError);

        // A rename can move this stack onto another stack's compose project name — or onto Watchtower's
        // own — which would make them share containers (and App API visibility). Checked before
        // anything is mutated.
        var projectName = StackMapping.ResolveProjectName(command.Name, command.ComposeProjectName);
        if (await StackProjectNames.ValidateAsync(db, selfProjects, projectName, excludeStackId: stack.Id, ct)
            is { } projectNameError)
            return AppError.Validation(projectNameError);

        stack.Name = command.Name;
        stack.BranchOverride =
            ProductSourceResolver.OverrideFor(command.Branch, ProductSourceResolver.InheritedBranch(stack));
        stack.ComposeProjectName = projectName;
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

    /// <summary>
    /// The message refusing a repository field the caller actually changed, or null when every one of
    /// them still matches the effective source. Empty inputs count as "not supplied" so a client that
    /// only sends the fields it edits keeps working.
    /// </summary>
    private static string? RefuseSourceChange(Command command, Product product, ProductSource effective) {
        if (Changed(command.RepositoryUrl, effective.RepositoryUrl))
            return Refusal("repository URL", product);
        if (Changed(command.ComposeFilePath, effective.ComposeFilePath))
            return Refusal("compose file path", product);
        // CredentialId has no "not supplied" form of its own — null means "no credential" as much as it
        // means "omitted" — so it is only refused when it names a different credential than the product's.
        if (command.CredentialId is { } credentialId && credentialId != effective.CredentialId)
            return Refusal("git credential", product);
        return null;
    }

    /// <summary>
    /// Both sides are trimmed, not just the supplied one: a stored value that carries padding — a
    /// backfilled row whose original column did — would otherwise never compare equal to what the form
    /// posts back, and the stack could never be saved at all.
    /// </summary>
    private static bool Changed(string? supplied, string effective) =>
        !string.IsNullOrWhiteSpace(supplied)
        && !string.Equals(supplied.Trim(), effective.Trim(), StringComparison.Ordinal);

    private static string Refusal(string field, Product product) => ProductCatalog.SourceRefusal(field, product);
}
