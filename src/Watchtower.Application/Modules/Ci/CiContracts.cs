using Watchtower.Application.Entities;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci;

/// <summary>A configured CI repo plus the orchestrator's live runner state.</summary>
public sealed record CiRepoDto(
    int Id,
    string Owner,
    string Name,
    string FullName,
    int CredentialId,
    bool Enabled,
    int MaxConcurrentRunners,
    string? RunnerImage,
    string? ExtraLabels,
    bool AllowDockerSocket,
    DateTimeOffset CreatedAt,
    CiRunnerStatusDto? RunnerStatus);

/// <summary>Live orchestrator state for one repo's runner slots.</summary>
public sealed record CiRunnerStatusDto(
    int DesiredRunners,
    int RunningRunners,
    long TotalSpawned,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    DateTimeOffset? BackoffUntil);

/// <summary>A repository the PAT can see, offered by the add-repo picker.</summary>
public sealed record CiAvailableRepoDto(string FullName, bool Private, string DefaultBranch, DateTimeOffset? PushedAt);

internal static class CiMapping {
    public const int MaxRunnersLimit = 16;

    public static CiRepoDto ToDto(CiRepo repo, CiRepoRunnerStatus? status) => new(
        repo.Id,
        repo.Owner,
        repo.Name,
        repo.FullName,
        repo.CredentialId,
        repo.Enabled,
        repo.MaxConcurrentRunners,
        repo.RunnerImage,
        repo.ExtraLabels,
        repo.AllowDockerSocket,
        repo.CreatedAt,
        status is null ? null : ToDto(status));

    public static CiRunnerStatusDto ToDto(CiRepoRunnerStatus status) => new(
        status.DesiredRunners,
        status.RunningRunners,
        status.TotalSpawned,
        status.LastError,
        status.LastErrorAt,
        status.BackoffUntil);

    /// <summary>Validates owner/name/runner-count invariants shared by add + update. Null when valid.</summary>
    public static string? Validate(string owner, string name, int maxRunners) {
        if (string.IsNullOrWhiteSpace(owner) || owner.Contains('/'))
            return $"Invalid repository owner: '{owner}'";
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/'))
            return $"Invalid repository name: '{name}'";
        if (maxRunners is < 1 or > MaxRunnersLimit)
            return $"MaxConcurrentRunners must be between 1 and {MaxRunnersLimit}.";
        return null;
    }
}
