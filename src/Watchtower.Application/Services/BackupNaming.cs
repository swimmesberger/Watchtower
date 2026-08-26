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
    /// A tenant's remote directory, relative to the provider base path:
    /// <c>{instance}/{product}/{tenant}</c>. Grouping by product is what makes a 200-tenant fleet's
    /// archives navigable on the storage — and it is why the value is <em>persisted</em>
    /// (<see cref="Entities.Stack.BackupDirectory"/>) rather than recomputed: a product rename would
    /// otherwise silently orphan every tenant's history at once.
    /// </summary>
    /// <param name="instanceName">The Watchtower instance name at the moment the stack was created.</param>
    /// <param name="productName">The product the tenant runs.</param>
    /// <param name="tenantSlug">The tenant's slug within its template.</param>
    public static string TenantDirectory(string instanceName, string productName, string tenantSlug) =>
        $"{Sanitize(instanceName)}/{Sanitize(productName)}/{Sanitize(tenantSlug)}";

    /// <summary>
    /// Where <paramref name="stack"/>'s archives live: the directory stamped on the row, or — for a
    /// stack created before the column existed — the value that has always been computed from the live
    /// instance name and the stack's current name.
    /// </summary>
    /// <remarks>
    /// The single answer to that question. All four sites that need it (the run, the restore download,
    /// the remote listing and retention) call this, so a stack cannot be written to one directory and
    /// listed from another. The legacy fallback is the pre-stage-7 behaviour byte for byte, which is what
    /// keeps an upgraded install's existing archives discoverable with no migration guessing at values
    /// SQL cannot know (the instance name is configuration, not a column).
    /// </remarks>
    /// <param name="stack">The stack.</param>
    /// <param name="instanceName">The live instance name, used only for the legacy fallback.</param>
    public static string ResolveDirectory(Entities.Stack stack, string instanceName) =>
        string.IsNullOrWhiteSpace(stack.BackupDirectory)
            ? StackDirectory(instanceName, stack.Name)
            : stack.BackupDirectory;

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
