namespace Watchtower.Application.Entities;

/// <summary>
/// One image a <see cref="Release"/> pins: a canonical repository and the manifest digest that
/// repository resolved to when the release was recorded.
/// </summary>
/// <remarks>
/// A child table rather than newline-packed text on the release, because "which release contains
/// repository X" and per-image availability are real queries (docs/products/design.md,
/// "Release and ReleaseImage").
/// </remarks>
public sealed class ReleaseImage {
    public int Id { get; set; }

    /// <summary>The release this image belongs to. Deleting the release deletes its images.</summary>
    public int ReleaseId { get; set; }
    public Release Release { get; set; } = null!;

    /// <summary>
    /// The canonical <c>{registry}/{repository}</c>, lower-cased
    /// (<see cref="Services.ImageRef.CanonicalRepository"/>) — e.g. <c>ghcr.io/acme/api</c>,
    /// <c>docker.io/library/nginx</c>. Unique per release: one build produces one image per repository.
    /// </summary>
    public required string Repository { get; set; }

    /// <summary>The tag the reporter named, when it named one. Kept for display; the digest is the pin.</summary>
    public string? Tag { get; set; }

    /// <summary>
    /// The manifest digest (<c>sha256:…</c>), resolved at intake for a tag reference and passed through
    /// for a digest one. Multi-arch images pin the manifest <em>index</em> digest, which is what the
    /// registry's <c>Docker-Content-Digest</c> header answers with.
    /// </summary>
    public required string Digest { get; set; }
}
