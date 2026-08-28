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
/// <param name="ReleaseMode">
/// The product's update mechanism, <c>"git"</c> or <c>"releases"</c> — the switch that decides which of
/// the two panels a stack page renders, never both (invariant 4).
/// </param>
/// <param name="HasUpdates">
/// <b>Means different things in the two modes, and must be read together with
/// <paramref name="ReleaseMode"/>.</b> In <c>"git"</c> mode: at least one container image has a newer
/// version in the registry, listed in <paramref name="OutdatedImages"/>. In <c>"releases"</c> mode: a
/// newer <em>release</em> exists, named by <paramref name="AvailableReleaseId"/> — no registry is
/// polled, so <paramref name="OutdatedImages"/> is empty and <paramref name="NewCommitSha"/> is
/// informational ("unreleased commits on the branch") rather than a reason to deploy. Null when the
/// stack has never been checked.
/// </param>
/// <param name="TrackingMode">
/// Derived, not stored: <c>"pinned"</c> when <paramref name="PinnedRelease"/> is set, else
/// <c>"latest"</c>. There is no tracking-mode column, and a nullable pin plus a derived label is why
/// there cannot be an invalid combination of the two.
/// </param>
/// <param name="PinnedRelease">The release this stack is pinned to, or null when it tracks latest.</param>
/// <param name="LastDeployedRelease">The release the last successful deploy applied, when there was one.</param>
/// <param name="AvailableReleaseId">
/// From the cached update check: the newer release, when one exists. Computed for pinned stacks too —
/// the pin chip shows how far behind it is — although automation ignores it there.
/// </param>
/// <param name="DriftedContainers">
/// From the cached update check: running containers that are not on the deployed release's images.
/// </param>
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
    DateTimeOffset? UpdatesCheckedAt,
    string ReleaseMode,
    string TrackingMode,
    StackReleaseRefDto? PinnedRelease,
    StackReleaseRefDto? LastDeployedRelease,
    int? AvailableReleaseId,
    string? AvailableReleaseVersion,
    string[]? DriftedContainers);

/// <summary>A release named on a stack: enough to render a chip, not enough to need a second call.</summary>
public sealed record StackReleaseRefDto(int Id, string Version);

/// <summary>A single deploy event for history display.</summary>
/// <param name="ReleaseId">
/// The release this deploy applied, stamped at execution time. Null for a <c>Git</c>-mode deploy, for
/// one that failed before the release was resolved, and for every deploy that ran before ADR-0026
/// stage 4.
/// </param>
/// <param name="ReleaseVersion">
/// Its label, denormalized beside the id so a history list renders a version chip per row without a
/// lookup per row — which is what kept the chip out of stage 4b.
/// </param>
public sealed record DeployEventDto(
    int Id, int StackId, string TriggeredBy, string Status, string? Output,
    DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    int? ReleaseId = null, string? ReleaseVersion = null);

/// <summary>A single environment variable key/value pair returned by the API.</summary>
public sealed record StackEnvVarDto(int Id, string Key, string Value);

/// <summary>One entry in a batch-replace request for stack environment variables.</summary>
public sealed record StackEnvVarInput(string Key, string Value);

/// <summary>One host device mapped into a compose service of a stack (ADR-0030).</summary>
public sealed record StackDeviceMappingDto(
    int Id, string Service, string HostPath, string ContainerPath, string? Permissions);

/// <summary>One entry in a batch-replace request for stack device mappings.</summary>
/// <param name="Service">The compose service name.</param>
/// <param name="HostPath">Absolute device path on the host.</param>
/// <param name="ContainerPath">Absolute device path in the container; null/blank defaults to <paramref name="HostPath"/>.</param>
/// <param name="Permissions">Cgroup permissions (subset of <c>rwm</c>); null/blank for the Docker default.</param>
public sealed record StackDeviceMappingInput(
    string Service, string HostPath, string? ContainerPath = null, string? Permissions = null);

/// <summary>Returned immediately after a deploy is accepted.</summary>
public sealed record DeployAcceptedDto(int DeployEventId, string Status);

/// <summary>In-memory projection helpers (not translatable to SQL).</summary>
public static class StackMapping {
    /// <summary>Enum → lowercase wire value: "git", "releases".</summary>
    public static string ReleaseModeToDto(ProductReleaseMode mode) => mode.ToString().ToLowerInvariant();

    /// <summary>The derived tracking label: a pin makes it "pinned", its absence "latest".</summary>
    /// <remarks>
    /// Derived rather than stored on purpose (ADR-0026's rejected <c>TrackingMode</c> enum): a column
    /// would add an invalid-state axis — "pinned with no pin" — for a distinction the DTO can compute.
    /// </remarks>
    public const string TrackingLatest = "latest";

    /// <inheritdoc cref="TrackingLatest"/>
    public const string TrackingPinned = "pinned";

    /// <summary>
    /// Projects a stack. <paramref name="s"/> must have its product loaded — and its template too when
    /// it is a tenant — because the source fields are resolved, not stored
    /// (<see cref="ProductSourceResolver"/>).
    /// </summary>
    /// <param name="s">The stack; <c>PinnedRelease</c> and <c>LastDeployedRelease</c> are projected when included.</param>
    /// <param name="check">The cached update check, when the caller loaded one.</param>
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
        check?.HasUpdates, check?.OutdatedImages, check?.NewCommitSha, check?.CheckedAt,
        ReleaseModeToDto(s.Product.ReleaseMode),
        s.PinnedReleaseId is null ? TrackingLatest : TrackingPinned,
        ReleaseRef(s.PinnedRelease),
        ReleaseRef(s.LastDeployedRelease),
        check?.AvailableReleaseId, check?.AvailableReleaseVersion, check?.DriftedContainers);
    }

    /// <summary>
    /// The chip for a release navigation, or null when it is absent — either because the stack has no
    /// such release or because the caller did not <c>Include</c> it.
    /// </summary>
    private static StackReleaseRefDto? ReleaseRef(Release? release) =>
        release is null ? null : new StackReleaseRefDto(release.Id, release.Version);

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

    /// <summary>
    /// Projects a deploy event. <paramref name="e"/> must have its <see cref="DeployEvent.Release"/>
    /// included for the version chip; without it the row renders chip-less rather than failing.
    /// </summary>
    public static DeployEventDto ToDto(DeployEvent e) =>
        new(e.Id, e.StackId, e.TriggeredBy, e.Status, e.Output, e.StartedAt, e.FinishedAt,
            e.ReleaseId, e.Release?.Version);

    /// <summary>Compose project name defaults to the stack name with spaces hyphenated.</summary>
    public static string ResolveProjectName(string name, string? explicitName) =>
        explicitName ?? name.ToLowerInvariant().Replace(' ', '-');

    /// <summary>Returns the first duplicate key in the list, or null when all keys are unique.</summary>
    public static string? FirstDuplicateKey(IEnumerable<StackEnvVarInput> vars) =>
        vars.GroupBy(v => v.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key;
}
