using System.Formats.Tar;
using System.Text;

namespace Watchtower.Application.Services;

/// <summary>What a scan of a backup tar found: the volume directories it contains, and the manifest.</summary>
public sealed record BackupArchiveContents(IReadOnlyList<string> Volumes, string? ManifestJson) {
    /// <summary>
    /// The database dumps the archive carries, as paths relative to <c>backup/</c>
    /// (<c>_dumps/db.sql</c>). An init-property rather than a positional member so a v1 archive — and
    /// every existing construction site — still reads as "no dumps" without saying so.
    /// </summary>
    public IReadOnlyList<string> DumpFiles { get; init; } = [];
}

/// <summary>
/// Reads a backup tar's table of contents without extracting anything (ADR-0016): the volume names
/// are the first-level directories under <c>backup/</c>, the manifest is
/// <c>backup/backup-manifest.json</c>, and the database dumps (ADR-0017) live under
/// <c>backup/_dumps/</c>. The restore flow scans first so it can compare the archive against the
/// volumes that exist on the host before touching any data.
/// </summary>
/// <remarks>
/// The table of contents is physical truth: a file is in the archive or it is not, whatever the
/// manifest claims. That is why <c>_dumps</c> is recognized here by name — an older Watchtower, which
/// knows nothing of dumps, reads it as a volume directory and would try to restore into a volume that
/// does not exist. It reports that as a skipped volume, which is the intended failure mode.
/// </remarks>
public static class BackupArchiveInspector {
    /// <summary>The reserved directory under <c>backup/</c> that holds the dumps, never a volume.</summary>
    internal const string DumpDirectory = "_dumps";

    /// <summary>Scans an <b>uncompressed</b> tar stream (decrypt/gunzip upstream of this call).</summary>
    public static async Task<BackupArchiveContents> InspectAsync(Stream tarStream, CancellationToken ct) {
        var volumes = new List<string>();
        var dumps = new List<string>();
        string? manifest = null;

        await using var reader = new TarReader(tarStream, leaveOpen: true);
        while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry) {
            var name = entry.Name.TrimStart('.', '/');
            if (!name.StartsWith("backup/", StringComparison.Ordinal)) continue;
            var rest = name["backup/".Length..].TrimEnd('/');
            if (rest.Length == 0) continue;

            if (rest == "backup-manifest.json" && entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile) {
                if (entry.DataStream is { } data) {
                    using var buffer = new MemoryStream();
                    await data.CopyToAsync(buffer, ct);
                    manifest = Encoding.UTF8.GetString(buffer.ToArray());
                }
                continue;
            }

            // The first path segment under backup/ is the volume directory. An entry directly under
            // backup/ counts only when it IS a directory — a stray file there is not a volume.
            var slash = rest.IndexOf('/');
            var first = slash < 0 ? rest : rest[..slash];
            if (first == DumpDirectory) {
                // Reserved: the dumps sit next to the volume directories, and a volume can never be
                // called _dumps because Docker forbids a leading underscore in a volume name.
                if (slash > 0 && entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
                    && !dumps.Contains(rest, StringComparer.Ordinal))
                    dumps.Add(rest);
                continue;
            }
            if (slash < 0 && entry.EntryType is not TarEntryType.Directory) continue;
            if (!volumes.Contains(first, StringComparer.Ordinal))
                volumes.Add(first);
        }

        volumes.Sort(StringComparer.Ordinal);
        dumps.Sort(StringComparer.Ordinal);
        return new BackupArchiveContents(volumes, manifest) { DumpFiles = dumps };
    }
}
