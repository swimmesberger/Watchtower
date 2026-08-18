namespace Watchtower.Application.Services;

/// <summary>
/// Backup storage in a directory inside the container — an operator-mounted second disk or network
/// share (ADR-0016 §3). Also the provider the tests exercise, since it needs no server.
/// </summary>
public sealed class LocalBackupStorage(string basePath) : IBackupStorage {
    public string Description => $"local:{basePath}";

    public async Task UploadAsync(
        string relativePath, Func<Stream, CancellationToken, Task> writer, CancellationToken ct) {
        var finalPath = Resolve(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);
        // Write under a partial name and move on success — a torn upload never looks finished.
        var partialPath = finalPath + ".partial";
        try {
            await using (var file = File.Create(partialPath))
                await writer(file, ct);
            File.Move(partialPath, finalPath, overwrite: true);
        } finally {
            if (File.Exists(partialPath)) File.Delete(partialPath);
        }
    }

    public Task<IReadOnlyList<string>> ListFileNamesAsync(string relativeDirectory, CancellationToken ct) {
        var dir = Resolve(relativeDirectory);
        IReadOnlyList<string> names = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().ToList()
            : [];
        return Task.FromResult(names);
    }

    public Task DeleteFileAsync(string relativePath, CancellationToken ct) {
        File.Delete(Resolve(relativePath));
        return Task.CompletedTask;
    }

    public void Dispose() { }

    /// <summary>
    /// Anchors a relative path under the base directory. The segments are produced by
    /// <see cref="BackupNaming.Sanitize"/>, but anchor anyway so no input can escape the base.
    /// </summary>
    private string Resolve(string relativePath) {
        var full = Path.GetFullPath(Path.Combine(basePath, relativePath));
        var root = Path.GetFullPath(basePath);
        if (!full.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException($"Path '{relativePath}' escapes the backup base directory.");
        return full;
    }
}
