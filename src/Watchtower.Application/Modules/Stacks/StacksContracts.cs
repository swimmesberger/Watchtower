using Watchtower.Application.Entities;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks;

/// <summary>Stack projection including last-deploy metadata and cached update-check results.</summary>
/// <remarks>
/// <paramref name="RepositoryUrl"/>, <paramref name="ComposeFilePath"/>, <paramref name="Branch"/> and
/// <paramref name="CredentialId"/> are <em>read-only projections of the effective source</em> since
/// ADR-0026 — they are resolved from the product (and the branch overrides) rather than stored on the
/// stack. They stay on the DTO so every existing reader — the frontend, the management API, scripts —
/// keeps working unchanged; writing them is what changed, and that is <c>products.update</c>'s job.
/// </remarks>
public sealed record StackDto(
    int Id,
    string Name,
    int ProductId,
    string ProductName,
    string RepositoryUrl,
    string ComposeFilePath,
    string Branch,
    string? BranchOverride,
    string ComposeProjectName,
    int? CredentialId,
    string? WebhookToken,
    bool WebhookEnabled,
    string AutoDeployMode,
    string? AutoDeployTime,
    string DesiredState,
    string? LastDeployStatus,
    DateTimeOffset? LastDeployedAt,
    string? LastDeployedCommit,
    DateTimeOffset CreatedAt,
    bool? HasUpdates,
    string[]? OutdatedImages,
    string? NewCommitSha,
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
    /// <summary>
    /// Projects a stack. <paramref name="s"/> must have its product loaded — and its template too when
    /// it is a tenant — because the source fields are resolved, not stored
    /// (<see cref="ProductSourceResolver"/>).
    /// </summary>
    public static StackDto ToDto(Stack s, StackUpdateCheck? check) {
        var source = ProductSourceResolver.Resolve(s);
        return new StackDto(
        s.Id, s.Name, s.ProductId, s.Product!.Name,
        source.RepositoryUrl, source.ComposeFilePath, source.Branch, s.BranchOverride,
        s.ComposeProjectName,
        source.CredentialId, s.WebhookToken, s.WebhookEnabled,
        ModeToDto(s.AutoDeployMode), s.AutoDeployTime,
        StateToDto(s.DesiredState),
        s.LastDeployStatus?.ToString().ToLowerInvariant(), s.LastDeployedAt, s.LastDeployedCommit, s.CreatedAt,
        check?.HasUpdates, check?.OutdatedImages, check?.NewCommitSha, check?.CheckedAt);
    }

    /// <summary>Enum → lowercase wire value: "running", "stopped".</summary>
    public static string StateToDto(StackDesiredState state) => state.ToString().ToLowerInvariant();

    /// <summary>Enum → camelCase wire value: "off", "onChange", "scheduled".</summary>
    public static string ModeToDto(AutoDeployMode mode) =>
        char.ToLowerInvariant(mode.ToString()[0]) + mode.ToString()[1..];

    /// <summary>Parses the camelCase wire value; null/empty means Off. Returns null when invalid.</summary>
    public static AutoDeployMode? ParseMode(string? mode) =>
        string.IsNullOrEmpty(mode) ? AutoDeployMode.Off
        : Enum.TryParse<AutoDeployMode>(mode, ignoreCase: true, out var parsed) ? parsed : null;

    /// <summary>
    /// Validates the auto-deploy pair and normalizes the time (null unless scheduled).
    /// Returns an error message, or null when valid.
    /// </summary>
    public static string? ValidateAutoDeploy(AutoDeployMode mode, ref string? time) {
        if (mode != AutoDeployMode.Scheduled) {
            time = null;
            return null;
        }
        return TimeOnly.TryParseExact(time, "HH:mm", out _)
            ? null
            : "Scheduled auto-deploy requires a time in HH:mm format (e.g. \"02:00\").";
    }

    public static DeployEventDto ToDto(DeployEvent e) =>
        new(e.Id, e.StackId, e.TriggeredBy, e.Status, e.Output, e.StartedAt, e.FinishedAt);

    /// <summary>Compose project name defaults to the stack name with spaces hyphenated.</summary>
    public static string ResolveProjectName(string name, string? explicitName) =>
        explicitName ?? name.ToLowerInvariant().Replace(' ', '-');

    /// <summary>Returns the first duplicate key in the list, or null when all keys are unique.</summary>
    public static string? FirstDuplicateKey(IEnumerable<StackEnvVarInput> vars) =>
        vars.GroupBy(v => v.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key;
}
