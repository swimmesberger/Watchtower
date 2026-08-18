namespace Watchtower.Application.Services;

/// <summary>
/// A backup storage backend (ADR-0016 §3): upload / list / delete on paths relative to the
/// provider's configured base directory. Instances are created per operation by
/// <see cref="BackupStorageFactory"/> from the current options and disposed afterwards — no
/// connection outlives the run that needed it.
/// </summary>
public interface IBackupStorage : IDisposable {
    /// <summary>Human-readable target description for logs and backup events (no secrets).</summary>
    string Description { get; }

    /// <summary>
    /// Creates <paramref name="relativePath"/> (parent directories included) and streams its content
    /// from <paramref name="writer"/>. Implementations upload to a temporary name and rename on
    /// completion, so a torn upload never looks like a finished backup.
    /// </summary>
    Task UploadAsync(string relativePath, Func<Stream, CancellationToken, Task> writer, CancellationToken ct);

    /// <summary>File names (no directories) directly inside <paramref name="relativeDirectory"/>; empty when it does not exist.</summary>
    Task<IReadOnlyList<string>> ListFileNamesAsync(string relativeDirectory, CancellationToken ct);

    /// <summary>Deletes one file. Missing files are an error — retention only deletes names it just listed.</summary>
    Task DeleteFileAsync(string relativePath, CancellationToken ct);
}
