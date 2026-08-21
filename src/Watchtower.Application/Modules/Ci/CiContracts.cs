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
    CiRunnerStatusDto? RunnerStatus,
    CiToolchainProfileDto? Toolchain);

/// <summary>Live orchestrator state for one repo's runner slots.</summary>
public sealed record CiRunnerStatusDto(
    int DesiredRunners,
    int RunningRunners,
    long TotalSpawned,
    string? LastError,
    DateTimeOffset? LastErrorAt,
    DateTimeOffset? BackoffUntil);

/// <summary>One detected toolchain of a CI repo ("dotnet 10.0 from workflow").</summary>
public sealed record CiToolchainDto(string Kind, string Version, string Source);

/// <summary>
/// The repo's detected toolchain profile plus the toolcache warm state derived from it. Null on a
/// <see cref="CiRepoDto"/> until a linked stack's deploy has run detection.
/// </summary>
/// <param name="WarmStatus">
/// <c>warmed</c> (toolcache matches the profile), <c>warming</c> (warmer container running),
/// <c>failed</c> (last warm attempt failed; see <paramref name="LastWarmError"/>), or
/// <c>pending</c> (warm not attempted yet).
/// </param>
public sealed record CiToolchainProfileDto(
    CiToolchainDto[] Toolchains,
    bool HasDockerfile,
    DateTimeOffset? DetectedAt,
    string WarmStatus,
    DateTimeOffset? LastWarmedAt,
    string? LastWarmError);

/// <summary>A repository the PAT can see, offered by the add-repo picker.</summary>
public sealed record CiAvailableRepoDto(string FullName, bool Private, string DefaultBranch, DateTimeOffset? PushedAt);

/// <summary>
/// The CI view of one stack: whether its repository can get runners at all (GitHub only), and the
/// linked <see cref="CiRepoDto"/> when CI is enabled. Multiple stacks of the same repository share
/// one CI repo — and therefore one runner pool and one toolcache.
/// </summary>
public sealed record CiStackCiDto(
    bool IsGitHub,
    string? Owner,
    string? Name,
    CiRepoDto? Repo);

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
        status is null ? null : ToDto(status),
        ToToolchainDto(repo, status));

    public static CiRunnerStatusDto ToDto(CiRepoRunnerStatus status) => new(
        status.DesiredRunners,
        status.RunningRunners,
        status.TotalSpawned,
        status.LastError,
        status.LastErrorAt,
        status.BackoffUntil);

    /// <summary>Projects the persisted profile + live warmer state into the wire DTO. Pure.</summary>
    public static CiToolchainProfileDto? ToToolchainDto(CiRepo repo, CiRepoRunnerStatus? status) {
        var profile = CiToolchainProfile.FromJson(repo.ToolchainProfileJson);
        if (profile is null)
            return null;
        var warmStatus =
            status?.WarmerRunning == true ? "warming"
            : profile.ComputeHash() == repo.WarmedProfileHash ? "warmed"
            : repo.LastWarmError is not null ? "failed"
            : "pending";
        return new CiToolchainProfileDto(
            profile.Toolchains.Select(t => new CiToolchainDto(t.Kind, t.Version, t.Source)).ToArray(),
            profile.HasDockerfile,
            repo.ToolchainDetectedAt,
            warmStatus,
            repo.LastWarmedAt,
            repo.LastWarmError);
    }

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
