using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>
/// Enables CI runners for the repository a stack deploys ("Enable CI" on the stack page). Parses
/// <c>owner/name</c> from the stack's GitHub repository URL and creates — or re-enables — the
/// matching <see cref="CiRepo"/>. Because CI repos are unique on <c>owner/name</c>, several stacks
/// of the same repository converge on one shared runner pool.
///
/// The credential needs more than the clone credential usually has: registering runners requires a
/// fine-grained PAT with repository <b>Administration (read and write)</b>, while cloning only needs
/// Contents (read). The chosen credential — explicit, or defaulting to the stack's clone
/// credential — is therefore probed up front, and a wrong-scoped PAT fails here with a message
/// naming the missing permission instead of failing silently in the reconcile loop.
/// </summary>
[Handler("ci.enableForStack")]
public sealed class EnableForStack(
    WatchtowerDbContext db,
    GitHubApiClient gitHub,
    CiRunnerOrchestrator orchestrator,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<EnableForStack.Command, Result<EnableForStack.Response>> {
    /// <param name="CredentialId">
    /// Credential holding the runner-admin PAT. Null uses the stack's clone credential.
    /// </param>
    public sealed record Command(int StackId, int? CredentialId = null);

    public sealed record Response(CiRepoDto Repo);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var stack = await db.Stacks.AsNoTracking().FirstOrDefaultAsync(s => s.Id == command.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.StackId} not found.");

        if (GitHubRepoUrl.TryParse(stack.RepositoryUrl) is not var (owner, name))
            return AppError.Validation(
                $"Stack '{stack.Name}' does not deploy from a github.com repository "
                + $"({stack.RepositoryUrl}). CI runners require GitHub Actions.");

        var credentialId = command.CredentialId ?? stack.CredentialId;
        if (credentialId is not { } resolvedCredentialId)
            return AppError.Validation(
                $"Stack '{stack.Name}' has no git credential to reuse. Choose a credential holding a "
                + "fine-grained PAT with repository Administration (read and write).");

        var credential = await db.Credentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == resolvedCredentialId, ct);
        if (credential is null)
            return AppError.NotFound($"Credential {resolvedCredentialId} not found.");

        var existing = await db.CiRepos.FirstOrDefaultAsync(
            r => r.Owner.ToLower() == owner.ToLower() && r.Name.ToLower() == name.ToLower(), ct);

        // Probe the PAT before touching anything, so misconfiguration fails with a precise message.
        // Skipped only when re-enabling an existing repo with its already-validated credential.
        var credentialChanged = existing is null || existing.CredentialId != resolvedCredentialId;
        if (credentialChanged
            && await gitHub.ValidateRepoAccessAsync(owner, name, credential.Token, ct) is { } accessError) {
            var hint = command.CredentialId is null
                ? $"The stack's clone credential '{credential.Name}' cannot manage runners: {accessError} "
                  + "Cloning only needs Contents (read); registering runners needs a fine-grained PAT with "
                  + "repository Administration (read and write). Choose or create a credential with that "
                  + "permission and try again."
                : $"Credential '{credential.Name}' cannot manage runners for {owner}/{name}: {accessError}";
            return AppError.Validation(hint);
        }

        CiRepo repo;
        if (existing is not null) {
            existing.Enabled = true;
            existing.CredentialId = resolvedCredentialId;
            repo = existing;
        } else {
            repo = new CiRepo {
                Owner = owner,
                Name = name,
                CredentialId = resolvedCredentialId,
                Enabled = true,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CiRepos.Add(repo);
        }
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync("ci", "repo.enable", repo.FullName,
            $"{(existing is null ? "enabled" : "re-enabled")} CI runners via stack '{stack.Name}' "
            + $"with credential '{credential.Name}'",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        orchestrator.RequestReconcile();
        var status = orchestrator.Status.TryGetValue(repo.Id, out var s) ? s : null;
        return new Response(CiMapping.ToDto(repo, status));
    }
}
