using System.Formats.Tar;
using System.Text;

namespace Watchtower.Application.Services;

/// <summary>What a scan of a backup tar found: the volume directories it contains, and the manifest.</summary>
public sealed record BackupArchiveContents(IReadOnlyList<string> Volumes, string? ManifestJson);

/// <summary>
/// Reads a backup tar's table of contents without extracting anything (ADR-0016): the volume names
/// are the first-level directories under <c>backup/</c>, the manifest is
/// <c>backup/backup-manifest.json</c>. The restore flow scans first so it can compare the archive
/// against the volumes that exist on the host before touching any data.
/// </summary>
public static class BackupArchiveInspector {
    /// <summary>Scans an <b>uncompressed</b> tar stream (decrypt/gunzip upstream of this call).</summary>
    public static async Task<BackupArchiveContents> InspectAsync(Stream tarStream, CancellationToken ct) {
        var volumes = new List<string>();
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
            if (slash < 0 && entry.EntryType is not TarEntryType.Directory) continue;
            var volume = slash < 0 ? rest : rest[..slash];
            if (!volumes.Contains(volume, StringComparer.Ordinal))
                volumes.Add(volume);
        }

        volumes.Sort(StringComparer.Ordinal);
        return new BackupArchiveContents(volumes, manifest);
    }
}
