using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>What the host knows about a locally present image.</summary>
/// <param name="Id">The <c>sha256:…</c> image id, as a running container reports it.</param>
/// <param name="RepoDigests">Manifest digests (<c>sha256:…</c>) the image was pulled under.</param>
public sealed record LocalImageState(string Id, IReadOnlyList<string> RepoDigests);

/// <summary>Result of a single stack update check (detached from EF tracking).</summary>
public sealed record StackUpdateResult(
    int StackId, bool HasUpdates, string[] OutdatedImages, string? NewCommitSha, DateTimeOffset CheckedAt) {
    /// <summary>True when a redeploy would pick up something new (newer image or new commit).</summary>
    public bool HasChanges => HasUpdates || NewCommitSha is not null;
}

/// <summary>
/// Checks whether a Docker Compose stack has something new to deploy: a newer container image in
/// the registry (same HEAD-manifest digest approach as <see cref="SelfUpdateService"/>) or a new
/// commit on the tracked git branch (<c>git ls-remote</c> vs. the last deployed commit).
/// Results are persisted to the <c>stack_update_checks</c> table via short-lived EF scopes, together
/// with the remote digest behind each outdated image so <see cref="RevalidateStackAsync"/> can later
/// clear the cached state locally.
/// </summary>
public class StackUpdateService(
    DockerEngineClient docker,
    GitCloneService git,
    IServiceScopeFactory scopeFactory,
    ILogger<StackUpdateService> logger) {

    /// <summary>
    /// Checks all stacks sequentially and stores results. Individual stack failures are
    /// logged and do not abort the remaining stacks.
    /// </summary>
    public async Task CheckAllStacksAsync(CancellationToken ct = default) {
        var allStacks = LoadAllStacks();
        logger.LogInformation("Starting stack update check for {Count} stack(s)", allStacks.Count);
        foreach (var stack in allStacks) {
            if (ct.IsCancellationRequested) break;
            try {
                await CheckStackAsync(stack, ct);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Stack update check failed for stack {StackName} (id={StackId})", stack.Name, stack.Id);
            }
        }
    }

    /// <summary>Checks a single stack and persists the result.</summary>
    public async Task<StackUpdateResult> CheckStackAsync(Stack stack, CancellationToken ct = default) {
        logger.LogInformation("Checking image updates for stack {StackName} (project={Project})", stack.Name, stack.ComposeProjectName);

        // Resolve optional registry credentials for this stack.
        string? username = null, token = null;
        if (stack.CredentialId is { } credId) {
            var cred = GetCredential(credId);
            if (cred is not null) (username, token) = cred.Value;
        }

        // Git: does the tracked branch have a commit newer than the last deploy? Runs even when
        // no containers are up — a compose-file change should still be detected and deployable.
        var newCommitSha = await CheckForNewCommitAsync(stack, token, ct);

        var projectContainers = await GetProjectContainersAsync(stack.ComposeProjectName, ct);

        if (projectContainers.Count == 0) {
            logger.LogDebug("No running containers found for stack {StackName}", stack.Name);
            return Upsert(stack.Id, hasUpdates: false, [], [], newCommitSha);
        }

        // Deduplicate image names (multiple replicas may share the same image).
        var imageNames = projectContainers
            .Select(c => c.Image)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var outdatedImages = new List<string>();
        var outdatedDigests = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var imageName in imageNames) {
            if (ct.IsCancellationRequested) break;
            try {
                var remoteDigest = await GetOutdatedRemoteDigestAsync(imageName, username, token, ct);
                if (remoteDigest is null) continue;
                outdatedImages.Add(imageName);
                outdatedDigests[imageName] = remoteDigest;
                logger.LogInformation("Outdated image detected in stack {StackName}: {Image}", stack.Name, imageName);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Could not check image {Image} for stack {StackName}", imageName, stack.Name);
            }
        }

        return Upsert(stack.Id, outdatedImages.Count > 0, [.. outdatedImages], outdatedDigests, newCommitSha);
    }

    /// <summary>
    /// Re-checks a stack's cached update state against what is actually running on the host, and clears
    /// the entries that no longer apply. Contacts no registry: an operator who ran
    /// <c>docker compose pull &amp;&amp; docker compose up -d</c> by hand already has the digest that made
    /// the entry outdated, and has a container running that image — both together are proof the update
    /// landed. A bare <c>pull</c> is deliberately not enough: the new image would be on disk while the
    /// old container keeps serving, which is exactly the state the badge exists to report.
    /// Commit-based state (<see cref="StackUpdateCheck.NewCommitSha"/>) and the last check timestamp are
    /// left untouched — neither can be judged without going out to the network.
    /// </summary>
    /// <returns>
    /// The updated result, or null when nothing changed: no cached row, no image-based updates, a row
    /// written before the remote digests were recorded (those self-correct on the next full check), or
    /// nothing that could be confirmed as applied.
    /// </returns>
    public virtual async Task<StackUpdateResult?> RevalidateStackAsync(int stackId, CancellationToken ct = default) {
        var check = LoadUpdateCheck(stackId);
        if (check is null || !check.HasUpdates || check.OutdatedImages.Length == 0) return null;
        if (check.OutdatedImageDigests.Count == 0) return null;
        var stack = LoadStack(stackId);
        if (stack is null) return null;

        var running = await GetProjectContainersAsync(stack.ComposeProjectName, ct);
        var runningImageIds = running
            .Select(c => c.ImageId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // What this pass positively confirmed as applied, and the digest it confirmed — the write below
        // only removes an entry the stored row still agrees about.
        var applied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var imageName in check.OutdatedImages) {
            if (ct.IsCancellationRequested) return null;
            // No recorded digest ⇒ nothing to compare against; leave the entry alone.
            if (!check.OutdatedImageDigests.TryGetValue(imageName, out var remoteDigest)) continue;
            var local = await InspectLocalImageAsync(imageName, ct);
            if (local is null) continue;
            if (!local.RepoDigests.Contains(remoteDigest, StringComparer.Ordinal)) continue;
            if (!runningImageIds.Contains(local.Id)) {
                logger.LogDebug(
                    "Image {Image} of stack {StackId} is pulled but no running container uses it yet",
                    imageName, stackId);
                continue;
            }
            logger.LogInformation(
                "Image {Image} of stack {StackId} was updated outside Watchtower; clearing its cached update flag",
                imageName, stackId);
            applied[imageName] = remoteDigest;
        }

        return applied.Count == 0 ? null : RemoveAppliedImages(stackId, applied);
    }

    /// <summary>
    /// Returns the remote branch head SHA when it differs from the stack's last deployed commit,
    /// otherwise null. Also null when the stack was never deployed (no baseline to compare against)
    /// or the remote can't be reached — a check failure must not trigger a deploy.
    /// </summary>
    private async Task<string?> CheckForNewCommitAsync(Stack stack, string? token, CancellationToken ct) {
        if (string.IsNullOrEmpty(stack.LastDeployedCommit)) return null;
        try {
            var remoteHead = await git.GetRemoteHeadAsync(stack.RepositoryUrl, stack.Branch, token, ct);
            if (remoteHead is null) {
                logger.LogDebug("Could not resolve remote head for stack {StackName} ({Branch})", stack.Name, stack.Branch);
                return null;
            }
            if (string.Equals(remoteHead, stack.LastDeployedCommit, StringComparison.OrdinalIgnoreCase))
                return null;
            logger.LogInformation(
                "New commit detected for stack {StackName}: {Remote} (deployed: {Deployed})",
                stack.Name, remoteHead, stack.LastDeployedCommit);
            return remoteHead;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogWarning(ex, "Git head check failed for stack {StackName}", stack.Name);
            return null;
        }
    }

    /// <summary>On-demand check for a single stack by id.</summary>
    /// <exception cref="KeyNotFoundException">When no stack with the given id exists.</exception>
    public async Task<StackUpdateResult> TriggerCheckAsync(int stackId, CancellationToken ct = default) {
        var stack = LoadStack(stackId) ?? throw new KeyNotFoundException($"Stack {stackId} not found.");
        return await CheckStackAsync(stack, ct);
    }

    /// <summary>
    /// The registry digest that makes <paramref name="imageName"/> outdated, or null when it is current,
    /// absent locally, or the registry could not be asked.
    /// </summary>
    private async Task<string?> GetOutdatedRemoteDigestAsync(
        string imageName, string? username, string? token, CancellationToken ct) {
        var remoteDigest = await GetRemoteDigestAsync(imageName, username, token, ct);
        if (string.IsNullOrWhiteSpace(remoteDigest)) {
            logger.LogDebug("Could not fetch remote digest for {Image}; skipping", imageName);
            return null;
        }

        var local = await InspectLocalImageAsync(imageName, ct);
        // Nothing local to compare against ⇒ not something a redeploy of this stack would change.
        if (local is null || local.RepoDigests.Count == 0) return null;
        return local.RepoDigests.Contains(remoteDigest, StringComparer.Ordinal) ? null : remoteDigest;
    }

    // ── Docker seams ──────────────────────────────────────────────────────────
    // The three calls this service makes out to the host, each virtual so tests can describe a host
    // without a daemon. GetRemoteDigestAsync is the only one that leaves the machine, which is what
    // makes "revalidation never touches a registry" checkable rather than merely intended.

    /// <summary>Running containers belonging to the stack's compose project.</summary>
    protected virtual async Task<IReadOnlyList<DockerContainerInfo>> GetProjectContainersAsync(
        string composeProjectName, CancellationToken ct) {
        var allContainers = await docker.ListContainersAsync(ct);
        return [.. allContainers.Where(c =>
            c.Labels.TryGetValue("com.docker.compose.project", out var project)
            && string.Equals(project, composeProjectName, StringComparison.OrdinalIgnoreCase))];
    }

    /// <summary>The locally present image, or null when it is absent or cannot be inspected.</summary>
    protected virtual async Task<LocalImageState?> InspectLocalImageAsync(string imageName, CancellationToken ct) {
        try {
            var localImage = await docker.InspectImageAsync(imageName, ct);
            return new LocalImageState(
                localImage.Id,
                [.. localImage.RepoDigests
                    .Where(rd => rd.Contains('@'))
                    .Select(rd => rd[(rd.IndexOf('@') + 1)..])
                    .Where(d => !string.IsNullOrWhiteSpace(d))]);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogDebug(ex, "Could not inspect local image {Image}", imageName);
            return null;
        }
    }

    /// <summary>The registry's current manifest digest for the image; null when it cannot be fetched.</summary>
    protected virtual Task<string?> GetRemoteDigestAsync(
        string imageName, string? username, string? token, CancellationToken ct) =>
        docker.GetRemoteDigestAsync(imageName, username, token, ct);

    // ── Scoped data access ────────────────────────────────────────────────────

    private List<Stack> LoadAllStacks() {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return db.Stacks.AsNoTracking().OrderBy(s => s.Name).ToList();
    }

    private Stack? LoadStack(int stackId) {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return db.Stacks.AsNoTracking().FirstOrDefault(s => s.Id == stackId);
    }

    private (string Username, string Token)? GetCredential(int credentialId) {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return db.Credentials.AsNoTracking()
            .Where(c => c.Id == credentialId)
            .Select(c => new ValueTuple<string, string>(c.Username, c.Token))
            .Cast<(string, string)?>()
            .FirstOrDefault();
    }

    private StackUpdateCheck? LoadUpdateCheck(int stackId) {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return db.StackUpdateChecks.AsNoTracking().FirstOrDefault(c => c.StackId == stackId);
    }

    private StackUpdateResult Upsert(
        int stackId, bool hasUpdates, string[] outdatedImages,
        Dictionary<string, string> outdatedImageDigests, string? newCommitSha) {
        var checkedAt = DateTimeOffset.UtcNow;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var existing = db.StackUpdateChecks.FirstOrDefault(c => c.StackId == stackId);
        if (existing is null) {
            db.StackUpdateChecks.Add(new StackUpdateCheck {
                StackId = stackId, HasUpdates = hasUpdates, OutdatedImages = outdatedImages,
                OutdatedImageDigests = outdatedImageDigests,
                NewCommitSha = newCommitSha, CheckedAt = checkedAt,
            });
        } else {
            existing.HasUpdates = hasUpdates;
            existing.OutdatedImages = outdatedImages;
            existing.OutdatedImageDigests = outdatedImageDigests;
            existing.NewCommitSha = newCommitSha;
            existing.CheckedAt = checkedAt;
        }
        db.SaveChanges();
        return new StackUpdateResult(stackId, hasUpdates, outdatedImages, newCommitSha, checkedAt);
    }

    /// <summary>
    /// Subtracts the confirmed-applied images from the stored row, leaving the commit state and the
    /// check timestamp as they were — a local revalidation is not a check.
    /// </summary>
    /// <remarks>
    /// A subtraction, never an assignment: a full check may have upserted a whole new image list while
    /// the Docker inspects above were running, and writing back a list derived from the pre-inspect
    /// snapshot would silently drop whatever that check just found. For the same reason an image is only
    /// removed while the stored row still names the digest this pass verified — a *newer* update
    /// published in the meantime is a different fact about the same image, and it stays.
    /// </remarks>
    private StackUpdateResult? RemoveAppliedImages(int stackId, Dictionary<string, string> applied) {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var existing = db.StackUpdateChecks.FirstOrDefault(c => c.StackId == stackId);
        if (existing is null) return null;

        var stale = existing.OutdatedImageDigests
            .Where(kv => applied.TryGetValue(kv.Key, out var verified) && verified == kv.Value)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (stale.Count == 0) return null;

        existing.OutdatedImages = [.. existing.OutdatedImages.Where(i => !stale.Contains(i))];
        existing.OutdatedImageDigests = existing.OutdatedImageDigests
            .Where(kv => !stale.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        existing.HasUpdates = existing.OutdatedImages.Length > 0;
        db.SaveChanges();
        return new StackUpdateResult(
            stackId, existing.HasUpdates, existing.OutdatedImages, existing.NewCommitSha, existing.CheckedAt);
    }
}
