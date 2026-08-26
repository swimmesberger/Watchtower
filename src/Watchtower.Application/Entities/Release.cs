namespace Watchtower.Application.Entities;

/// <summary>
/// One build of a <see cref="Product"/> (ADR-0026 decision 3): the git commit it was built from plus
/// the manifest digests of the images that build produced (<see cref="Images"/>). A release is the
/// unit a stack will later deploy or pin to — reproducible, because both halves are recorded.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Id"/> is the ordering key: latest is the highest id. Monotonic and clock-skew safe,
/// which is why <see cref="CreatedAt"/> and <see cref="PublishedAt"/> are display values and are
/// never ordered on — two instances writing releases a second apart must not be able to invert the
/// order by disagreeing about the time.
/// </para>
/// <para>
/// Idempotency is keyed on <see cref="Fingerprint"/> rather than on <see cref="CommitSha"/>: a
/// retried webhook call produces the identical fingerprint and is answered with the existing
/// release, while a genuine rebuild of the same commit onto new base-image layers produces new
/// digests and is therefore a new release — the case a commit-keyed rule would wrongly swallow.
/// </para>
/// </remarks>
public sealed class Release {
    /// <summary><c>CreatedVia</c> for a release reported by the product's CI through the webhook.</summary>
    public const string ViaWebhook = "webhook";

    /// <summary><c>CreatedVia</c> for a release an operator recorded by hand (<c>products.createRelease</c>).</summary>
    public const string ViaManual = "manual";

    /// <summary>Identity, and the ordering key — see the remarks on this type.</summary>
    public int Id { get; set; }

    /// <summary>The product this is a build of. Deleting the product deletes its releases.</summary>
    public int ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>Display label, unique per product. Defaults to the short commit SHA at intake.</summary>
    public required string Version { get; set; }

    /// <summary>
    /// The 40-hex commit the build came from, or null when the release records only images (a
    /// poll-discovered release in a later stage): the clone then falls back to the branch head.
    /// </summary>
    public string? CommitSha { get; set; }

    /// <summary>The branch the build came from, validated against the product's branch at intake.</summary>
    public required string Branch { get; set; }

    /// <summary>
    /// The idempotency key: <c>sha256(commit + "\n" + sorted "repository@digest" lines)</c>, lower-case
    /// hex — see <c>ReleaseFingerprint</c> for the exact construction. Unique per product, and the
    /// unique index is the enforcement; the pre-check only exists for the error message.
    /// </summary>
    public required string Fingerprint { get; set; }

    /// <summary>Link back to the CI run that reported this release, when it supplied one.</summary>
    public string? SourceRunUrl { get; set; }

    /// <summary>Free-text notes — release notes, or why an operator recorded this by hand.</summary>
    public string? Notes { get; set; }

    /// <summary>
    /// How the release arrived: <see cref="ViaWebhook"/> or <see cref="ViaManual"/>. Stored as the
    /// string rather than an enum so a later source (polling) is an additive value, not a migration.
    /// </summary>
    public required string CreatedVia { get; set; }

    /// <summary>When Watchtower recorded the release. Display only, never ordering.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// When the build itself was published, if the reporter said. Display only, never ordering — see
    /// the remarks on this type.
    /// </summary>
    public DateTimeOffset? PublishedAt { get; set; }

    /// <summary>The images this build produced, one row per repository.</summary>
    public ICollection<ReleaseImage> Images { get; set; } = [];
}
