using System.Security.Cryptography;
using System.Text;

namespace Watchtower.Application.Services;

/// <summary>
/// The idempotency key of a release: <c>sha256(commit + "\n" + sorted "repository@digest" lines)</c>,
/// lower-case hex (ADR-0026 decision 3).
/// </summary>
/// <remarks>
/// <para>
/// Keyed on what actually changes. A retried <c>curl</c> re-sends the same commit and the same digests
/// and therefore hashes to the same value, so the webhook answers with the release that already
/// exists; a genuine rebuild of the same commit onto newer base layers produces different digests and
/// is a new release — which a commit-keyed rule would have swallowed.
/// </para>
/// <para>
/// Exactly what goes in, in order: the commit (lower-cased, empty string when the release has none),
/// then one <c>\n</c>, then the <c>{canonical repository}@{digest}</c> lines joined by <c>\n</c> and
/// sorted ordinally. The sort is what makes the value independent of the order the images were
/// reported in — two workflows listing the same two images the other way round must not produce two
/// releases. There is no trailing newline.
/// </para>
/// </remarks>
public static class ReleaseFingerprint {
    /// <summary>
    /// The fingerprint for a commit and a set of resolved images.
    /// </summary>
    /// <param name="commitSha">The commit, or null for a release that records only images.</param>
    /// <param name="images">The <c>(canonical repository, digest)</c> pairs, in any order.</param>
    public static string Compute(string? commitSha, IEnumerable<(string Repository, string Digest)> images) {
        var lines = images
            .Select(i => $"{i.Repository}@{i.Digest}")
            .OrderBy(line => line, StringComparer.Ordinal);
        var payload = new StringBuilder(commitSha?.ToLowerInvariant() ?? string.Empty)
            .Append('\n')
            .AppendJoin('\n', lines)
            .ToString();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>
    /// Whether <paramref name="value"/> is a full 40-character hexadecimal commit SHA. Abbreviated
    /// SHAs are refused deliberately: a release pins a checkout, and an abbreviation is ambiguous by
    /// construction.
    /// </summary>
    public static bool IsCommitSha(string? value) =>
        value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary>
    /// The version a release defaults to when the reporter named none: the seven-character short SHA,
    /// the form git itself prints and the one a person recognizes in a list.
    /// </summary>
    public static string ShortSha(string commitSha) =>
        commitSha.Length <= 7 ? commitSha : commitSha[..7].ToLowerInvariant();

    /// <summary>A commit rendered for an audit detail line, or <c>none</c> when there is none.</summary>
    public static string DescribeCommit(string? commitSha) =>
        commitSha is null ? "none" : ShortSha(commitSha);
}
