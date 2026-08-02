using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>
/// Enables CI runners for a GitHub repository. Validates the credential's API access up front
/// (fine-grained PAT with repository Administration write) so misconfiguration fails here with a
/// clear message rather than silently in the reconcile loop.
/// </summary>
[Handler("ci.addRepo")]
public sealed class AddRepo(
    WatchtowerDbContext db,
    GitHubApiClient gitHub,
    CiRunnerOrchestrator orchestrator)
    : IHandler<AddRepo.Command, Result<AddRepo.Response>> {
    public sealed record Command(
        string Owner,
        string Name,
        int CredentialId,
        bool Enabled = true,
        int MaxConcurrentRunners = 1,
        string? RunnerImage = null,
        string? ExtraLabels = null,
        bool AllowDockerSocket = false);

    public sealed record Response(CiRepoDto Repo);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var owner = command.Owner.Trim();
        var name = command.Name.Trim();
        if (CiMapping.Validate(owner, name, command.MaxConcurrentRunners) is { } invalid)
            return AppError.Validation(invalid);

        var credential = await db.Credentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == command.CredentialId, ct);
        if (credential is null)
            return AppError.NotFound($"Credential {command.CredentialId} not found.");

        var exists = await db.CiRepos.AnyAsync(r => r.Owner == owner && r.Name == name, ct);
        if (exists)
            return AppError.Conflict($"Repository {owner}/{name} is already configured.");

        if (await gitHub.ValidateRepoAccessAsync(owner, name, credential.Token, ct) is { } accessError)
            return AppError.Validation(accessError);

        var repo = new CiRepo {
            Owner = owner,
            Name = name,
            CredentialId = command.CredentialId,
            Enabled = command.Enabled,
            MaxConcurrentRunners = command.MaxConcurrentRunners,
            RunnerImage = NullIfBlank(command.RunnerImage),
            ExtraLabels = NullIfBlank(command.ExtraLabels),
            AllowDockerSocket = command.AllowDockerSocket,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CiRepos.Add(repo);
        await db.SaveChangesAsync(ct);

        orchestrator.RequestReconcile();
        return new Response(CiMapping.ToDto(repo, status: null));
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
