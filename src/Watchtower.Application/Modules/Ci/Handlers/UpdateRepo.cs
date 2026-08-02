using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>Updates a CI repo's runner settings; the orchestrator converges on the next pass.</summary>
[Handler("ci.updateRepo")]
public sealed class UpdateRepo(WatchtowerDbContext db, CiRunnerOrchestrator orchestrator)
    : IHandler<UpdateRepo.Command, Result<UpdateRepo.Response>> {
    public sealed record Command(
        int Id,
        bool Enabled,
        int MaxConcurrentRunners,
        int CredentialId,
        string? RunnerImage,
        string? ExtraLabels,
        bool AllowDockerSocket);

    public sealed record Response(CiRepoDto Repo);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var repo = await db.CiRepos.FirstOrDefaultAsync(r => r.Id == command.Id, ct);
        if (repo is null)
            return AppError.NotFound($"CI repo {command.Id} not found.");

        if (CiMapping.Validate(repo.Owner, repo.Name, command.MaxConcurrentRunners) is { } invalid)
            return AppError.Validation(invalid);

        var credentialExists = await db.Credentials.AnyAsync(c => c.Id == command.CredentialId, ct);
        if (!credentialExists)
            return AppError.NotFound($"Credential {command.CredentialId} not found.");

        repo.Enabled = command.Enabled;
        repo.MaxConcurrentRunners = command.MaxConcurrentRunners;
        repo.CredentialId = command.CredentialId;
        repo.RunnerImage = string.IsNullOrWhiteSpace(command.RunnerImage) ? null : command.RunnerImage.Trim();
        repo.ExtraLabels = string.IsNullOrWhiteSpace(command.ExtraLabels) ? null : command.ExtraLabels.Trim();
        repo.AllowDockerSocket = command.AllowDockerSocket;
        await db.SaveChangesAsync(ct);

        orchestrator.RequestReconcile();
        var status = orchestrator.Status.TryGetValue(repo.Id, out var s) ? s : null;
        return new Response(CiMapping.ToDto(repo, status));
    }
}
