using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>Updates a CI repo's runner settings; the orchestrator converges on the next pass.</summary>
[Handler("ci.updateRepo")]
public sealed class UpdateRepo(
    WatchtowerDbContext db,
    CiRunnerOrchestrator orchestrator,
    RegistryAuthBuilder registryAuth,
    GitHubApiClient gitHub,
    AuditLog audit,
    ICurrentUser currentUser)
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

        var credential = await db.Credentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == command.CredentialId, ct);
        if (credential is null)
            return AppError.NotFound($"Credential {command.CredentialId} not found.");

        var syncRegistryUrl = string.IsNullOrWhiteSpace(command.SyncRegistryUrl) ? null : command.SyncRegistryUrl.Trim();
        var selectionChanged = !string.Equals(syncRegistryUrl, repo.SyncRegistryUrl, StringComparison.OrdinalIgnoreCase);
        if (syncRegistryUrl is not null && selectionChanged) {
            // Selection-time guard: the URL must resolve to usable credentials right now, so a typo
            // or credential-less registry fails here instead of as a background sync error.
            var resolved = (await registryAuth.ListResolvedRegistriesAsync(ct))
                .FirstOrDefault(r => string.Equals(r.Url, syncRegistryUrl, StringComparison.OrdinalIgnoreCase));
            if (resolved is null)
                return AppError.Validation($"No registry '{syncRegistryUrl}' is known — it is neither a "
                    + "Watchtower registry nor present in the host docker config.");
            if (resolved.Username is null || resolved.Password is null)
                return AppError.Validation($"Registry '{syncRegistryUrl}' has no usable credentials to sync "
                    + "(credential-helper entries in the host docker config cannot be read).");
        }
        // Same up-front discipline as the runner-admin probe in ci.enableForStack: syncing needs PAT
        // permissions plain runners don't (Secrets + Variables), so probe when the selection or the
        // credential changes while a sync registry is in play. The sync itself stays optional — no
        // registry selected, no extra permissions asked of the PAT.
        if (syncRegistryUrl is not null && (selectionChanged || repo.CredentialId != command.CredentialId)
            && await gitHub.ValidateSecretsAccessAsync(
                repo.Owner, repo.Name, credential.Token, CiActionsConfigSync.RegistryFeature, ct)
                is { } accessError) {
            return AppError.Validation(
                $"Credential '{credential.Name}' cannot sync registry credentials for {repo.FullName}: {accessError}");
        }
        if (selectionChanged) {
            // Changed (or cleared) selection: drop the sync state so the orchestrator re-pushes.
            repo.RegistrySyncedHash = null;
            repo.RegistrySyncedAt = null;
            repo.LastRegistrySyncError = null;
        }

        // Field-level diff for the audit trail, collected before the assignments overwrite it.
        var changes = new List<string>();
        if (repo.Enabled != command.Enabled)
            changes.Add(command.Enabled ? "enabled" : "disabled");
        if (repo.MaxConcurrentRunners != command.MaxConcurrentRunners)
            changes.Add($"max runners {repo.MaxConcurrentRunners} → {command.MaxConcurrentRunners}");
        if (repo.CredentialId != command.CredentialId)
            changes.Add($"credential → '{credential.Name}'");
        var runnerImage = string.IsNullOrWhiteSpace(command.RunnerImage) ? null : command.RunnerImage.Trim();
        if (repo.RunnerImage != runnerImage)
            changes.Add($"runner image → {runnerImage ?? "default"}");
        var extraLabels = string.IsNullOrWhiteSpace(command.ExtraLabels) ? null : command.ExtraLabels.Trim();
        if (repo.ExtraLabels != extraLabels)
            changes.Add($"extra labels → {extraLabels ?? "none"}");
        if (repo.AllowDockerSocket != command.AllowDockerSocket)
            changes.Add(command.AllowDockerSocket ? "docker socket mounted" : "docker socket unmounted");
        if (selectionChanged)
            changes.Add(syncRegistryUrl is null ? "registry sync off" : $"registry sync → '{syncRegistryUrl}'");

        repo.SyncRegistryUrl = syncRegistryUrl;
        repo.Enabled = command.Enabled;
        repo.MaxConcurrentRunners = command.MaxConcurrentRunners;
        repo.CredentialId = command.CredentialId;
        repo.RunnerImage = runnerImage;
        repo.ExtraLabels = extraLabels;
        repo.AllowDockerSocket = command.AllowDockerSocket;
        await db.SaveChangesAsync(ct);

        if (changes.Count > 0) {
            await audit.RecordAsync("ci", "repo.update", repo.FullName, string.Join("; ", changes),
                actor: await audit.ActorAsync(currentUser, ct), ct: ct);
        }

        // Every save also drops a standing sync-failure defer: a config change is the operator
        // saying "try again now" — typically right after granting the PAT the missing permissions.
        orchestrator.ClearActionsSyncBackoff(repo.Id);
        orchestrator.RequestReconcile();
        var status = orchestrator.Status.TryGetValue(repo.Id, out var s) ? s : null;
        return new Response(CiMapping.ToDto(repo, status));
    }
}
