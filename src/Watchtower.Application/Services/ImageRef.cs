using System.Diagnostics.CodeAnalysis;

namespace Watchtower.Application.Services;

/// <summary>
/// A container image reference, split into its registry host, repository path and the tag or digest it
/// names — and normalized the way a registry client and an image-pinning match both have to normalize
/// it (docs/products/design.md, "Image pinning").
/// </summary>
/// <remarks>
/// <para>
/// Pure and runtime-neutral: it names no Docker or Compose concept and performs no I/O, so the same
/// parse serves the registry HEAD in <see cref="DockerEngineClient.GetRemoteDigestAsync"/> and any
/// later "does this compose service run one of the release's images?" comparison. One parser is the
/// whole point — a second, subtly different one is how pinning silently stops matching for somebody's
/// <c>localhost:5000</c> registry.
/// </para>
/// <para>
/// Only the canonical form is lower-cased. <see cref="Registry"/> is a DNS host and therefore
/// case-insensitive anyway, but <see cref="Repository"/> keeps the case it was written with, because
/// it is what a registry request path is built from and the caller may be talking to a registry that
/// does not agree with Docker Hub about case. <see cref="Tag"/> and <see cref="Digest"/> are
/// case-sensitive by specification and are never touched.
/// </para>
/// </remarks>
/// <param name="Registry">
/// The registry host, lower-cased, with the Docker Hub aliases resolved — never empty. See
/// <see cref="DockerHubRegistry"/>.
/// </param>
/// <param name="Repository">
/// The repository path with Docker Hub's implicit <c>library/</c> namespace applied, in the case it was
/// written with (e.g. <c>library/nginx</c>, <c>acme/api</c>).
/// </param>
/// <param name="Tag">The tag, or null when the reference carries none.</param>
/// <param name="Digest">The <c>sha256:…</c> digest, or null when the reference carries none.</param>
public sealed record ImageRef(string Registry, string Repository, string? Tag, string? Digest) {
    /// <summary>The canonical name of Docker Hub, and the registry an unqualified image belongs to.</summary>
    public const string DockerHubRegistry = "docker.io";

    /// <summary>The namespace Docker Hub gives an image written without one (<c>nginx</c> ⇒ <c>library/nginx</c>).</summary>
    public const string DockerHubLibraryNamespace = "library";

    /// <summary>The tag a reference naming neither a tag nor a digest resolves to.</summary>
    public const string DefaultTag = "latest";

    /// <summary>
    /// Hosts that are Docker Hub under another name: the v1 index and the v2 registry endpoint. Both
    /// normalize to <see cref="DockerHubRegistry"/> so <c>index.docker.io/acme/api</c> and
    /// <c>acme/api</c> compare equal.
    /// </summary>
    private static readonly string[] DockerHubAliases = ["index.docker.io", "registry-1.docker.io"];

    /// <summary>
    /// <c>{registry}/{repository}</c>, lower-cased — the identity two references are compared on, with
    /// the tag and digest deliberately left out: a release pins <em>which build</em> of a repository
    /// runs, so the repository is what has to match.
    /// </summary>
    public string CanonicalRepository =>
        $"{Registry}/{Repository}".ToLowerInvariant();

    /// <summary>
    /// What a registry request names this image by: the digest when there is one, otherwise the tag,
    /// otherwise <see cref="DefaultTag"/>.
    /// </summary>
    public string Reference => Digest ?? Tag ?? DefaultTag;

    /// <summary>True when this image lives on Docker Hub.</summary>
    public bool IsDockerHub => string.Equals(Registry, DockerHubRegistry, StringComparison.Ordinal);

    /// <summary>Parses <paramref name="image"/>, throwing when it is not a usable reference.</summary>
    /// <exception cref="FormatException">The reference could not be parsed.</exception>
    public static ImageRef Parse(string image) =>
        TryParse(image, out var result)
            ? result
            : throw new FormatException($"'{image}' is not a valid image reference.");

    /// <summary>
    /// Parses <paramref name="image"/> into its normalized parts. Returns false — rather than a guess —
    /// for anything that is not a usable reference.
    /// </summary>
    /// <remarks>
    /// The order of the four steps is what makes the ambiguous cases come out right:
    /// <list type="number">
    /// <item>the <c>@sha256:…</c> digest comes off first, so its own colon cannot be mistaken for a tag
    /// separator;</item>
    /// <item>the tag is the last <c>:</c> occurring <em>after</em> the last <c>/</c>, so the port in
    /// <c>registry:5000/img</c> is a port and not a tag;</item>
    /// <item>the first path segment is a registry host only when it contains a <c>.</c> or a <c>:</c>,
    /// or is exactly <c>localhost</c> — Docker's own rule, and what keeps <c>acme/api</c> on Docker Hub
    /// while <c>localhost:5000/api</c> stays local;</item>
    /// <item>the Docker Hub aliases collapse onto <see cref="DockerHubRegistry"/>, and a Hub image
    /// written without a namespace gains <c>library/</c>.</item>
    /// </list>
    /// </remarks>
    /// <param name="image">The reference, e.g. <c>ghcr.io/acme/api:v1</c> or <c>nginx</c>.</param>
    /// <param name="result">The parsed reference, or null when this returns false.</param>
    /// <returns>True when <paramref name="image"/> was parsed.</returns>
    public static bool TryParse(string? image, [NotNullWhen(true)] out ImageRef? result) {
        result = null;
        if (string.IsNullOrWhiteSpace(image)) return false;
        var text = image.Trim();

        // 1. The digest, before anything else looks for a colon.
        string? digest = null;
        var at = text.LastIndexOf('@');
        if (at >= 0) {
            digest = text[(at + 1)..];
            text = text[..at];
            if (text.Length == 0 || !IsDigest(digest)) return false;
        }

        // 2. The tag: the last colon that comes after the last slash, so a registry port is not one.
        string? tag = null;
        var lastColon = text.LastIndexOf(':');
        if (lastColon > text.LastIndexOf('/')) {
            tag = text[(lastColon + 1)..];
            text = text[..lastColon];
            if (tag.Length == 0 || text.Length == 0) return false;
        }

        // 3. The registry host, when the first segment looks like one.
        string registry;
        string repository;
        var firstSlash = text.IndexOf('/');
        if (firstSlash > 0 && IsRegistryHost(text[..firstSlash])) {
            registry = text[..firstSlash].ToLowerInvariant();
            repository = text[(firstSlash + 1)..];
        } else {
            registry = DockerHubRegistry;
            repository = text;
        }
        if (repository.Length == 0) return false;

        // 4. Docker Hub's aliases and its implicit namespace.
        if (DockerHubAliases.Contains(registry, StringComparer.Ordinal)) registry = DockerHubRegistry;
        if (string.Equals(registry, DockerHubRegistry, StringComparison.Ordinal)
            && !repository.Contains('/', StringComparison.Ordinal))
            repository = $"{DockerHubLibraryNamespace}/{repository}";

        result = new ImageRef(registry, repository, tag, digest);
        return true;
    }

    /// <summary>
    /// The registry host a configured registry URL names, in the same normalized form
    /// <see cref="Registry"/> carries — so an entry of the resolved registry view can be matched
    /// against an image's registry.
    /// </summary>
    /// <remarks>
    /// The view's keys are whatever docker config or an operator wrote down:
    /// <c>https://index.docker.io/v1/</c>, <c>ghcr.io</c>, <c>https://registry.example.com</c>,
    /// <c>localhost:5000</c>. Scheme and path are dropped, the host is lower-cased, and the Docker Hub
    /// aliases collapse onto <see cref="DockerHubRegistry"/> — the same four rules
    /// <see cref="TryParse"/> applies, which is the point of having this here rather than at the call
    /// site.
    /// </remarks>
    /// <param name="registryUrl">A registry URL or bare host; null or blank yields an empty string.</param>
    public static string NormalizeRegistryHost(string? registryUrl) {
        if (string.IsNullOrWhiteSpace(registryUrl)) return string.Empty;
        var text = registryUrl.Trim();
        var scheme = text.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) text = text[(scheme + 3)..];
        var slash = text.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0) text = text[..slash];
        text = text.ToLowerInvariant();
        return DockerHubAliases.Contains(text, StringComparer.Ordinal) ? DockerHubRegistry : text;
    }

    /// <summary>
    /// The OCI digest shape: an algorithm, a colon, and a lower-case hexadecimal encoding of at least
    /// 32 characters (<c>sha256:…</c>, and <c>sha512:…</c> without naming either).
    /// </summary>
    /// <remarks>
    /// Checked rather than assumed because a digest is a value later stages <em>compare</em>: an image
    /// written <c>api@latest</c> by mistake must fail to parse here, not arrive as a
    /// <see cref="Digest"/> of "latest" that then matches nothing and pins nothing, silently. The
    /// algorithm is not restricted to <c>sha256</c> — a registry answering with another one is the
    /// registry's business, and rejecting it would refuse a reference that is perfectly valid.
    /// </remarks>
    private static bool IsDigest(string digest) {
        var separator = digest.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0) return false;
        var encoded = digest[(separator + 1)..];
        return encoded.Length >= 32
            && encoded.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    /// <summary>
    /// Docker's rule for telling a registry host from the first component of a repository path: a host
    /// carries a dot or a port, or is <c>localhost</c>. Without it <c>acme/api</c> — a Docker Hub image
    /// — would read as the host <c>acme</c>.
    /// </summary>
    private static bool IsRegistryHost(string segment) =>
        segment.Contains('.', StringComparison.Ordinal)
        || segment.Contains(':', StringComparison.Ordinal)
        || string.Equals(segment, "localhost", StringComparison.OrdinalIgnoreCase);
}
