using Watchtower.Application.Entities;

namespace Watchtower.Application.Modules.Stacks;

/// <summary>Stack projection including last-deploy metadata and cached update-check results.</summary>
public sealed record StackDto(
    int Id,
    string Name,
    string RepositoryUrl,
    string ComposeFilePath,
    string Branch,
    string ComposeProjectName,
    int? CredentialId,
    string? WebhookToken,
    bool WebhookEnabled,
    string? LastDeployStatus,
    DateTimeOffset? LastDeployedAt,
    DateTimeOffset CreatedAt,
    bool? HasUpdates,
    string[]? OutdatedImages,
    DateTimeOffset? UpdatesCheckedAt);

/// <summary>A single deploy event for history display.</summary>
public sealed record DeployEventDto(
    int Id, int StackId, string TriggeredBy, string Status, string? Output,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

/// <summary>A single environment variable key/value pair returned by the API.</summary>
public sealed record StackEnvVarDto(int Id, string Key, string Value);

/// <summary>One entry in a batch-replace request for stack environment variables.</summary>
public sealed record StackEnvVarInput(string Key, string Value);

/// <summary>Returned immediately after a deploy is accepted.</summary>
public sealed record DeployAcceptedDto(int DeployEventId, string Status);

/// <summary>In-memory projection helpers (not translatable to SQL).</summary>
public static class StackMapping {
    public static StackDto ToDto(Stack s, StackUpdateCheck? check) => new(
        s.Id, s.Name, s.RepositoryUrl, s.ComposeFilePath, s.Branch, s.ComposeProjectName,
        s.CredentialId, s.WebhookToken, s.WebhookEnabled,
        s.LastDeployStatus?.ToString().ToLowerInvariant(), s.LastDeployedAt, s.CreatedAt,
        check?.HasUpdates, check?.OutdatedImages, check?.CheckedAt);

    public static DeployEventDto ToDto(DeployEvent e) =>
        new(e.Id, e.StackId, e.TriggeredBy, e.Status, e.Output, e.StartedAt, e.FinishedAt);

    /// <summary>Compose project name defaults to the stack name with spaces hyphenated.</summary>
    public static string ResolveProjectName(string name, string? explicitName) =>
        explicitName ?? name.ToLowerInvariant().Replace(' ', '-');

    /// <summary>Returns the first duplicate key in the list, or null when all keys are unique.</summary>
    public static string? FirstDuplicateKey(IEnumerable<StackEnvVarInput> vars) =>
        vars.GroupBy(v => v.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key;
}
