using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>Updates a CI repo's runner settings; the orchestrator converges on the next pass.</summary>
[Handler("ci.updateRepo")]
public sealed class UpdateRepo(WatchtowerDbContext db, CiRunnerOrchestrator orchestrator, RegistryAuthBuilder registryAuth)
    : IHandler<UpdateRepo.Command, Result<UpdateRepo.Response>> {
    /// <param name="SyncRegistryUrl">
    /// Registry (by URL, from the merged host + Watchtower registry view) whose credentials the
    /// orchestrator syncs to the repo's GitHub Actions config. Null turns the sync off — already
    /// pushed values stay at GitHub.
    /// </param>
    public sealed record Command(
        int Id,
        bool Enabled,
        int MaxConcurrentRunners,
        int CredentialId,
        string? RunnerImage,
        string? ExtraLabels,
        bool AllowDockerSocket,
        string? SyncRegistryUrl = null);

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

        var syncRegistryUrl = string.IsNullOrWhiteSpace(command.SyncRegistryUrl) ? null : command.SyncRegistryUrl.Trim();
        if (syncRegistryUrl is not null
            && !string.Equals(syncRegistryUrl, repo.SyncRegistryUrl, StringComparison.OrdinalIgnoreCase)) {
            // Selection-time guard: the URL must resolve to usable credentials right now, so a typo
            // or credential-less registry fails here instead of as a background sync error.
            var resolved = registryAuth.ListResolvedRegistries()
                .FirstOrDefault(r => string.Equals(r.Url, syncRegistryUrl, StringComparison.OrdinalIgnoreCase));
            if (resolved is null)
                return AppError.Validation($"No registry '{syncRegistryUrl}' is known — it is neither a "
                    + "Watchtower registry nor present in the host docker config.");
            if (resolved.Username is null || resolved.Password is null)
                return AppError.Validation($"Registry '{syncRegistryUrl}' has no usable credentials to sync "
                    + "(credential-helper entries in the host docker config cannot be read).");
        }
        if (!string.Equals(syncRegistryUrl, repo.SyncRegistryUrl, StringComparison.OrdinalIgnoreCase)) {
            // Changed (or cleared) selection: drop the sync state so the orchestrator re-pushes.
            repo.RegistrySyncedHash = null;
            repo.RegistrySyncedAt = null;
            repo.LastRegistrySyncError = null;
        }
        repo.SyncRegistryUrl = syncRegistryUrl;

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
