namespace Watchtower.Application.Entities;

/// <summary>Cached result of a stack image update check. One row per stack (PK is the stack id).</summary>
/// <remarks>
/// The row means different things in the two product modes (docs/products/design.md §"Update checks and
/// drift"), which is why it carries both vocabularies rather than reinterpreting one:
/// <list type="bullet">
/// <item><b><c>Git</c> mode</b> — <see cref="HasUpdates"/> means "a newer image digest is in the
/// registry", listed in <see cref="OutdatedImages"/>; <see cref="NewCommitSha"/> is a second, equal
/// trigger. The release columns are null.</item>
/// <item><b><c>Releases</c> mode</b> — <see cref="HasUpdates"/> means "a newer release exists", named by
/// <see cref="AvailableReleaseId"/>/<see cref="AvailableReleaseVersion"/>; no registry is polled at all,
/// so <see cref="OutdatedImages"/> stays empty. <see cref="DriftedContainers"/> replaces it with a local
/// question, and <see cref="NewCommitSha"/> becomes purely informational ("unreleased commits on the
/// branch") and never sets <see cref="HasUpdates"/> by itself.</item>
/// </list>
/// </remarks>
public sealed class StackUpdateCheck {
    public int StackId { get; set; }
    public Stack? Stack { get; set; }
    /// <summary>
    /// In <c>Git</c> mode: at least one container image in the stack has a newer version in the
    /// registry. In <c>Releases</c> mode: a newer release exists (<see cref="AvailableReleaseId"/> is
    /// set) — see the remarks on this type.
    /// </summary>
    public bool HasUpdates { get; set; }
    /// <summary>Image names (with tag) that have a newer version available. Persisted as newline-separated text.</summary>
    public string[] OutdatedImages { get; set; } = [];
    /// <summary>
    /// The remote manifest digest (<c>sha256:…</c>) that made each entry of <see cref="OutdatedImages"/>
    /// outdated, keyed by image reference. Recorded so the cached state can later be revalidated against
    /// the images actually present on the host — an operator who pulls and recreates a stack by hand ends
    /// up with that digest locally, which clears the entry without contacting the registry again.
    /// Persisted as newline-separated <c>"&lt;image&gt; &lt;digest&gt;"</c> lines (neither part contains whitespace).
    /// Empty on rows written before this was recorded; those can only be corrected by a full check.
    /// </summary>
    public Dictionary<string, string> OutdatedImageDigests { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Remote branch head SHA when it differs from the last deployed commit (a new commit is available).
    /// Null when the branch is up to date, was never deployed, or could not be checked.
    /// </summary>
    public string? NewCommitSha { get; set; }
    /// <summary>
    /// <c>Releases</c> mode only: the newest release of the stack's product when it is not the one the
    /// stack last deployed. Null in <c>Git</c> mode, and when the stack is already on the newest
    /// release.
    /// </summary>
    /// <remarks>
    /// Computed for pinned stacks too — the pin chip shows how far behind it is — but automation ignores
    /// it there (design.md §"Auto-deploy precedence", rule 2). Deliberately <em>not</em> a foreign key:
    /// this is a cache row, and a value that stops matching a live release is corrected by the next
    /// check rather than by a schema rule that would make deleting a release harder.
    /// </remarks>
    public int? AvailableReleaseId { get; set; }
    /// <summary>
    /// The version label of <see cref="AvailableReleaseId"/>, denormalized so a stack list renders
    /// "v2026.8-14 available" without joining releases per row. Null whenever that id is.
    /// </summary>
    public string? AvailableReleaseVersion { get; set; }
    /// <summary>
    /// <c>Releases</c> mode only: names of running containers whose image is not one of the digests the
    /// stack's deployed release pins — the local answer to "is this stack really running v42?".
    /// Persisted as newline-separated text, like <see cref="OutdatedImages"/>.
    /// </summary>
    /// <remarks>
    /// A pure <c>docker inspect</c> comparison (design.md §"Update checks and drift"): the registry is
    /// never asked, because comparing a pinned digest against a moving tag would report "outdated"
    /// forever and fight the pin. Empty in <c>Git</c> mode, and whenever the stack has no deployed
    /// release to compare against.
    /// </remarks>
    public string[] DriftedContainers { get; set; } = [];
    public DateTimeOffset CheckedAt { get; set; }
}
