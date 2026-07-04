using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>Result of a single stack image update check (detached from EF tracking).</summary>
public sealed record StackUpdateResult(int StackId, bool HasUpdates, string[] OutdatedImages, DateTimeOffset CheckedAt);

/// <summary>
/// Checks whether any container image in a Docker Compose stack has a newer version available
/// in the registry, using the same HEAD-manifest digest approach as <see cref="SelfUpdateService"/>.
/// Results are persisted to the <c>stack_update_checks</c> table via short-lived EF scopes.
/// </summary>
public sealed class StackUpdateService(
    DockerEngineClient docker,
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

        // List all running containers belonging to this compose project.
        var allContainers = await docker.ListContainersAsync(ct);
        var projectContainers = allContainers
            .Where(c => c.Labels.TryGetValue("com.docker.compose.project", out var proj)
                        && string.Equals(proj, stack.ComposeProjectName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (projectContainers.Count == 0) {
            logger.LogDebug("No running containers found for stack {StackName}", stack.Name);
            return Upsert(stack.Id, hasUpdates: false, []);
        }

        // Deduplicate image names (multiple replicas may share the same image).
        var imageNames = projectContainers
            .Select(c => c.Image)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var outdatedImages = new List<string>();
        foreach (var imageName in imageNames) {
            if (ct.IsCancellationRequested) break;
            try {
                if (await IsImageOutdatedAsync(imageName, username, token, ct)) {
                    outdatedImages.Add(imageName);
                    logger.LogInformation("Outdated image detected in stack {StackName}: {Image}", stack.Name, imageName);
                }
            } catch (Exception ex) {
                logger.LogWarning(ex, "Could not check image {Image} for stack {StackName}", imageName, stack.Name);
            }
        }

        return Upsert(stack.Id, outdatedImages.Count > 0, [.. outdatedImages]);
    }

    /// <summary>On-demand check for a single stack by id.</summary>
    /// <exception cref="KeyNotFoundException">When no stack with the given id exists.</exception>
    public async Task<StackUpdateResult> TriggerCheckAsync(int stackId, CancellationToken ct = default) {
        var stack = LoadStack(stackId) ?? throw new KeyNotFoundException($"Stack {stackId} not found.");
        return await CheckStackAsync(stack, ct);
    }

    private async Task<bool> IsImageOutdatedAsync(string imageName, string? username, string? token, CancellationToken ct) {
        var remoteDigest = await docker.GetRemoteDigestAsync(imageName, username, token, ct);
        if (string.IsNullOrWhiteSpace(remoteDigest)) {
            logger.LogDebug("Could not fetch remote digest for {Image}; skipping", imageName);
            return false;
        }

        string? localDigest = null;
        try {
            var localImage = await docker.InspectImageAsync(imageName, ct);
            localDigest = localImage.RepoDigests
                .Select(rd => rd.Contains('@') ? rd[(rd.IndexOf('@') + 1)..] : null)
                .FirstOrDefault(d => d is not null);
        } catch (Exception ex) {
            logger.LogDebug(ex, "Could not inspect local image {Image}", imageName);
        }

        return localDigest is not null && localDigest != remoteDigest;
    }

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

    private StackUpdateResult Upsert(int stackId, bool hasUpdates, string[] outdatedImages) {
        var checkedAt = DateTimeOffset.UtcNow;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var existing = db.StackUpdateChecks.FirstOrDefault(c => c.StackId == stackId);
        if (existing is null) {
            db.StackUpdateChecks.Add(new StackUpdateCheck {
                StackId = stackId, HasUpdates = hasUpdates, OutdatedImages = outdatedImages, CheckedAt = checkedAt,
            });
        } else {
            existing.HasUpdates = hasUpdates;
            existing.OutdatedImages = outdatedImages;
            existing.CheckedAt = checkedAt;
        }
        db.SaveChanges();
        return new StackUpdateResult(stackId, hasUpdates, outdatedImages, checkedAt);
    }
}
