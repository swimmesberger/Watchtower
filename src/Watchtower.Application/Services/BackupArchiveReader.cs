using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Watchtower.Application.Services;

/// <summary>
/// Opens a spooled backup archive for reading: file → optional decrypt → gunzip → tar. Shared by the
/// stack restore (ADR-0016) and the instance restore (ADR-0027), which read the same format for
/// different reasons and must not drift on how a damaged or wrongly-keyed archive is reported.
/// </summary>
/// <remarks>
/// Takes the passphrase directly rather than the options: an instance restore decrypts with the
/// passphrase from the <em>bundle</em> it was handed, which is by definition not the one this instance
/// is configured with.
/// </remarks>
public static class BackupArchiveReader {
    /// <summary>
    /// Runs <paramref name="action"/> over the archive as an uncompressed tar stream, disposing every
    /// layer afterwards — including the file handle, which a caller's delete depends on.
    /// </summary>
    /// <param name="archivePath">The spooled archive on disk.</param>
    /// <param name="passphrase">The passphrase it was encrypted with, or null when it is not encrypted.</param>
    /// <param name="action">What to do with the tar stream.</param>
    public static async Task<T> WithArchiveAsync<T>(
        string archivePath, string? passphrase, Func<Stream, Task<T>> action) {
        await using var file = File.OpenRead(archivePath);
        var inner = passphrase is { Length: > 0 }
            ? BackupEncryption.CreateDecryptingStream(file, passphrase)
            : file;
        try {
            await using var tar = new GZipStream(inner, CompressionMode.Decompress, leaveOpen: true);
            return await action(tar);
        } finally {
            if (!ReferenceEquals(inner, file)) await inner.DisposeAsync();
        }
    }

    /// <summary>
    /// Scans the archive's table of contents, translating a decode failure on an encrypted archive into
    /// the question an operator can actually answer.
    /// </summary>
    /// <param name="archivePath">The spooled archive on disk.</param>
    /// <param name="passphrase">The passphrase it was encrypted with, or null when it is not encrypted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The archive could not be decoded.</exception>
    public static Task<BackupArchiveContents> ReadContentsAsync(
        string archivePath, string? passphrase, CancellationToken ct) =>
        WithArchiveAsync(archivePath, passphrase, async tar => {
            try {
                return await BackupArchiveInspector.InspectAsync(tar, ct);
            } catch (Exception ex) when (passphrase is { Length: > 0 }
                && ex is InvalidDataException or CryptographicException) {
                throw new InvalidOperationException(
                    "Could not read the encrypted archive — is the encryption passphrase the one it was "
                    + $"written with? ({ex.Message})");
            }
        });

    /// <summary>
    /// Copies one file out of the archive into <paramref name="destination"/>. Read straight from the
    /// archive rather than kept from an earlier pass: a dump is arbitrarily large and has no business
    /// in memory.
    /// </summary>
    /// <param name="archivePath">The spooled archive on disk.</param>
    /// <param name="passphrase">The passphrase it was encrypted with, or null when it is not encrypted.</param>
    /// <param name="relativeFile">The path inside the archive's <c>backup/</c> root, e.g. <c>_dumps/db.sql</c>.</param>
    /// <param name="destination">Host path to write; created and overwritten.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The size of the extracted file.</returns>
    /// <exception cref="InvalidOperationException">The archive does not contain that file.</exception>
    public static Task<long> ExtractAsync(
        string archivePath, string? passphrase, string relativeFile, string destination, CancellationToken ct) =>
        WithArchiveAsync(archivePath, passphrase, async tar => {
            var wanted = $"backup/{relativeFile}";
            await using var reader = new TarReader(tar, leaveOpen: true);
            while (await reader.GetNextEntryAsync(cancellationToken: ct) is { } entry) {
                if (!string.Equals(entry.Name.TrimStart('.', '/'), wanted, StringComparison.Ordinal)) continue;
                if (entry.DataStream is not { } data) break;
                await using var file = File.Create(destination);
                await data.CopyToAsync(file, ct);
                return file.Length;
            }
            // The table-of-contents scan found it a moment ago, so this means the archive changed under
            // us or is damaged — either way, not something to restore from.
            throw new InvalidOperationException(
                $"The archive does not contain '{wanted}', although its table of contents lists it.");
        });
}
