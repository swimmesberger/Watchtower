using System.Globalization;
using System.Text.RegularExpressions;

namespace Watchtower.Application.Services;

/// <summary>
/// The remote layout and file-name format of stack backups (ADR-0016 §3):
/// <c>{instance}/{stack}/{project}_{yyyyMMdd'T'HHmmss'Z'}.tar.gz[.enc]</c>, rooted in the provider's
/// base path. The timestamp is UTC and lexicographically sortable; retention parses it back out of
/// the name so it needs no remote metadata, and only names matching this pattern are ever considered
/// for deletion.
/// </summary>
public static partial class BackupNaming {
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";

    [GeneratedRegex(@"_(?<ts>\d{8}T\d{6}Z)\.tar\.gz(\.enc)?$")]
    private static partial Regex FileNamePattern();

    /// <summary>The archive file name for a backup taken at <paramref name="takenAtUtc"/>.</summary>
    public static string FileName(string composeProject, DateTimeOffset takenAtUtc, bool encrypted) =>
        $"{Sanitize(composeProject)}_{takenAtUtc.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture)}"
        + ".tar.gz" + (encrypted ? ".enc" : "");

    /// <summary>The stack's remote directory, relative to the provider base path.</summary>
    public static string StackDirectory(string instanceName, string stackName) =>
        $"{Sanitize(instanceName)}/{Sanitize(stackName)}";

    /// <summary>
    /// Extracts the UTC timestamp from a backup file name, or null when the name does not match the
    /// backup pattern (foreign files must never take part in retention).
    /// </summary>
    public static DateTimeOffset? ParseTimestamp(string fileName) {
        var match = FileNamePattern().Match(fileName);
        if (!match.Success) return null;
        return DateTimeOffset.TryParseExact(
            match.Groups["ts"].Value, TimestampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts)
            ? ts
            : null;
    }

    /// <summary>
    /// Makes a name safe as a single path segment on any storage backend: everything outside
    /// letters, digits, <c>.</c>, <c>-</c> and <c>_</c> becomes <c>-</c>, and the segment can never
    /// be empty or a dot-only traversal.
    /// </summary>
    public static string Sanitize(string name) {
        var cleaned = string.Concat(name.Trim().Select(c =>
            char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-'));
        cleaned = cleaned.Trim('.');
        return cleaned.Length == 0 ? "unnamed" : cleaned;
    }
}
