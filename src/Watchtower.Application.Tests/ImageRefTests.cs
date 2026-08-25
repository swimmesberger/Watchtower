using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="ImageRef"/> — the one normalization both the registry digest lookup and image
/// pinning go through.
/// </summary>
/// <remarks>
/// Every case here is one where a second, slightly different parser would disagree: where the tag ends
/// when the registry carries a port, whether the first path segment is a host, and which spellings of
/// Docker Hub mean the same registry. A disagreement is not a visible failure — it is a pin that
/// silently stops matching somebody's <c>localhost:5000</c> registry — so the rules are pinned here
/// rather than left to the two call sites.
/// </remarks>
public sealed class ImageRefTests {
    [Theory]
    // An unqualified name is Docker Hub's library namespace, with no tag written.
    [InlineData("nginx", "docker.io", "library/nginx", null, null)]
    [InlineData("nginx:1.27", "docker.io", "library/nginx", "1.27", null)]
    // Two segments and no dot in the first: still Docker Hub, not a host called "acme".
    [InlineData("acme/api:v1", "docker.io", "acme/api", "v1", null)]
    [InlineData("ghcr.io/acme/api:v1", "ghcr.io", "acme/api", "v1", null)]
    // A host is a host because it has a dot, a port, or is localhost.
    [InlineData("localhost/api", "localhost", "api", null, null)]
    [InlineData("localhost:5000/x", "localhost:5000", "x", null, null)]
    [InlineData("registry:5000/acme/api:v1", "registry:5000", "acme/api", "v1", null)]
    // Deeper paths (Harbor, GitLab, Nexus) keep every segment.
    [InlineData("registry.example.com:8443/team/group/api:2026.8", "registry.example.com:8443", "team/group/api", "2026.8", null)]
    public void Parse_SplitsAReferenceIntoItsParts(
        string image, string registry, string repository, string? tag, string? digest) {
        var parsed = ImageRef.Parse(image);

        Assert.Equal(registry, parsed.Registry);
        Assert.Equal(repository, parsed.Repository);
        Assert.Equal(tag, parsed.Tag);
        Assert.Equal(digest, parsed.Digest);
    }

    /// <summary>
    /// A port is not a tag. The colon that separates a tag comes after the last slash; the one in
    /// <c>registry:5000/img</c> does not, so this image is untagged and its host carries a port.
    /// </summary>
    [Fact]
    public void Parse_DoesNotMistakeARegistryPortForATag() {
        var parsed = ImageRef.Parse("registry:5000/img");

        Assert.Null(parsed.Tag);
        Assert.Equal("registry:5000", parsed.Registry);
        Assert.Equal("img", parsed.Repository);
        // Nothing was named, so a registry request would ask for :latest.
        Assert.Equal("latest", parsed.Reference);
    }

    /// <summary>A manifest digest as a registry returns it: an algorithm and 64 hexadecimal characters.</summary>
    private const string Digest = "sha256:9f2c1d3e4b5a60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9";

    [Theory]
    [InlineData("nginx@" + Digest, "docker.io", "library/nginx", null)]
    [InlineData("ghcr.io/acme/api@" + Digest, "ghcr.io", "acme/api", null)]
    // Both forms at once: the digest comes off first, so its own colon is never read as a tag.
    [InlineData("ghcr.io/acme/api:v1@" + Digest, "ghcr.io", "acme/api", "v1")]
    [InlineData("localhost:5000/x:dev@" + Digest, "localhost:5000", "x", "dev")]
    public void Parse_TakesTheDigestOffBeforeLookingForATag(
        string image, string registry, string repository, string? tag) {
        const string digest = Digest;
        var parsed = ImageRef.Parse(image);

        Assert.Equal(registry, parsed.Registry);
        Assert.Equal(repository, parsed.Repository);
        Assert.Equal(tag, parsed.Tag);
        Assert.Equal(digest, parsed.Digest);
        // A digest names the manifest exactly, so it wins over the tag written beside it.
        Assert.Equal(digest, parsed.Reference);
    }

    /// <summary>
    /// The three spellings of Docker Hub have to compare equal, or an image pinned as
    /// <c>index.docker.io/acme/api</c> stops matching the same stack's <c>acme/api</c>.
    /// </summary>
    [Theory]
    [InlineData("acme/api")]
    [InlineData("docker.io/acme/api")]
    [InlineData("index.docker.io/acme/api")]
    [InlineData("registry-1.docker.io/acme/api")]
    public void Parse_CollapsesEveryNameForDockerHub(string image) {
        var parsed = ImageRef.Parse(image);

        Assert.Equal("docker.io", parsed.Registry);
        Assert.True(parsed.IsDockerHub);
        Assert.Equal("docker.io/acme/api", parsed.CanonicalRepository);
    }

    /// <summary>The implicit <c>library/</c> namespace is Docker Hub's, and only Docker Hub's.</summary>
    [Theory]
    [InlineData("nginx", "docker.io/library/nginx")]
    [InlineData("index.docker.io/nginx", "docker.io/library/nginx")]
    [InlineData("quay.io/nginx", "quay.io/nginx")]
    public void CanonicalRepository_AppliesTheLibraryNamespaceOnlyOnDockerHub(string image, string canonical) =>
        Assert.Equal(canonical, ImageRef.Parse(image).CanonicalRepository);

    /// <summary>
    /// The canonical form is what a match is decided on, so it is lower-cased — while the repository
    /// itself keeps the case it was written with, because that is what a registry request path is
    /// built from.
    /// </summary>
    [Fact]
    public void CanonicalRepository_IsLowerCasedWhileTheRepositoryKeepsItsCase() {
        var parsed = ImageRef.Parse("GHCR.IO/Acme/API:V1");

        Assert.Equal("ghcr.io", parsed.Registry);
        Assert.Equal("Acme/API", parsed.Repository);
        Assert.Equal("ghcr.io/acme/api", parsed.CanonicalRepository);
        // A tag is case-sensitive by specification and is never rewritten.
        Assert.Equal("V1", parsed.Tag);
    }

    /// <summary>The repository is the identity: two builds of one image compare equal by it.</summary>
    [Fact]
    public void CanonicalRepository_IgnoresTheTagAndTheDigest() {
        Assert.Equal(
            ImageRef.Parse("ghcr.io/acme/api:v1").CanonicalRepository,
            ImageRef.Parse($"ghcr.io/acme/api@{Digest}").CanonicalRepository);
    }

    /// <summary>
    /// A digest is a value the pinning stages compare, so anything that is not one is refused here
    /// rather than carried along as a <c>Digest</c> that would silently match nothing.
    /// </summary>
    [Theory]
    [InlineData("nginx@garbage")]
    [InlineData("nginx@latest")]
    [InlineData("nginx@sha256:")]
    [InlineData("nginx@sha256:abc123")]                     // too short to be an encoded digest
    [InlineData("nginx@sha256:ZZZZ1d3e4b5a60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9")]
    [InlineData("nginx@9f2c1d3e4b5a60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f9")] // no algorithm
    public void TryParse_RefusesAReferenceWhoseDigestIsNotOne(string image) =>
        Assert.False(ImageRef.TryParse(image, out _));

    /// <summary>The algorithm is not restricted to SHA-256; only the shape is checked.</summary>
    [Fact]
    public void Parse_AcceptsADigestUnderAnotherAlgorithm() {
        var digest = "sha512:" + new string('a', 128);

        Assert.Equal(digest, ImageRef.Parse($"ghcr.io/acme/api@{digest}").Digest);
    }

    /// <summary>An image a release never built matches nothing, so it is left alone — no allowlist needed.</summary>
    [Fact]
    public void CanonicalRepository_OfAStockImageIsNotOneAReleaseWouldPublish() =>
        Assert.Equal("docker.io/library/postgres", ImageRef.Parse("postgres:16").CanonicalRepository);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nginx:")]
    [InlineData("nginx@")]
    [InlineData(":tag")]
    [InlineData("@sha256:abc")]
    public void TryParse_RefusesWhatIsNotAReference(string? image) {
        Assert.False(ImageRef.TryParse(image, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void Parse_ThrowsOnWhatTryParseRefuses() =>
        Assert.Throws<FormatException>(() => ImageRef.Parse("nginx:"));

    /// <summary>Surrounding whitespace is a compose file's, not part of the name.</summary>
    [Fact]
    public void Parse_TrimsTheReference() =>
        Assert.Equal("ghcr.io/acme/api", ImageRef.Parse("  ghcr.io/acme/api:v1  ").CanonicalRepository);
}
