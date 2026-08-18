using System.Text;
using Renci.SshNet;
using Renci.SshNet.Sftp;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// SFTP backup storage (ADR-0016 §3) over SSH.NET — the swappable-vendor target: any host with an
/// sshd (a Hetzner Storage Box, a NAS, another server) works. Password and private-key auth may be
/// combined; the connection is opened lazily on first use and lives for the run that created this
/// instance. SSH.NET's SFTP surface is synchronous — acceptable here because every caller is a
/// background run (or an explicit connection test), never a request hot path.
/// </summary>
public sealed class SftpBackupStorage(SftpBackupOptions options) : IBackupStorage {
    private SftpClient? _client;

    public string Description =>
        $"sftp://{options.Username}@{options.Host}:{options.Port}/{BasePath}";

    private string BasePath => (options.BasePath ?? "").Trim().TrimEnd('/');

    public async Task UploadAsync(
        string relativePath, Func<Stream, CancellationToken, Task> writer, CancellationToken ct) {
        var client = Connect();
        var finalPath = Resolve(relativePath);
        CreateDirectories(client, PosixParent(finalPath));
        // Upload under a partial name and rename on success — a torn upload never looks finished.
        var partialPath = finalPath + ".partial";
        try {
            await using (var remote = client.Open(partialPath, FileMode.Create, FileAccess.Write))
                await writer(remote, ct);
            if (client.Exists(finalPath)) client.DeleteFile(finalPath);
            client.RenameFile(partialPath, finalPath);
        } catch {
            try {
                if (client.Exists(partialPath)) client.DeleteFile(partialPath);
            } catch {
                // Best-effort cleanup; the original failure is the one worth reporting.
            }
            throw;
        }
    }

    public Task<IReadOnlyList<string>> ListFileNamesAsync(string relativeDirectory, CancellationToken ct) {
        var client = Connect();
        var dir = Resolve(relativeDirectory);
        if (!client.Exists(dir)) return Task.FromResult<IReadOnlyList<string>>([]);
        IReadOnlyList<string> names = client.ListDirectory(dir)
            .Where(f => f.IsRegularFile)
            .Select(f => f.Name)
            .ToList();
        return Task.FromResult(names);
    }

    public Task DeleteFileAsync(string relativePath, CancellationToken ct) {
        Connect().DeleteFile(Resolve(relativePath));
        return Task.CompletedTask;
    }

    public void Dispose() {
        _client?.Dispose();
        _client = null;
    }

    private string Resolve(string relativePath) {
        var path = relativePath.Trim('/');
        return BasePath.Length == 0 ? path : $"{BasePath}/{path}";
    }

    private static string PosixParent(string path) {
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "" : path[..idx];
    }

    /// <summary>Creates a directory path segment by segment (SFTP mkdir is single-level).</summary>
    private static void CreateDirectories(SftpClient client, string path) {
        if (path.Length == 0) return;
        var current = new StringBuilder();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            if (current.Length > 0) current.Append('/');
            current.Append(segment);
            var dir = current.ToString();
            if (!client.Exists(dir)) client.CreateDirectory(dir);
        }
    }

    private SftpClient Connect() {
        if (_client is { IsConnected: true }) return _client;
        _client?.Dispose();
        _client = new SftpClient(BuildConnectionInfo());
        _client.Connect();
        return _client;
    }

    private ConnectionInfo BuildConnectionInfo() {
        if (string.IsNullOrWhiteSpace(options.Host))
            throw new InvalidOperationException("SFTP backup storage is not configured: Host is empty.");
        if (string.IsNullOrWhiteSpace(options.Username))
            throw new InvalidOperationException("SFTP backup storage is not configured: Username is empty.");

        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrEmpty(options.PrivateKey)) {
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(options.PrivateKey));
            var keyFile = string.IsNullOrEmpty(options.PrivateKeyPassphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, options.PrivateKeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(options.Username, keyFile));
        }
        if (!string.IsNullOrEmpty(options.Password))
            methods.Add(new PasswordAuthenticationMethod(options.Username, options.Password));
        if (methods.Count == 0)
            throw new InvalidOperationException(
                "SFTP backup storage is not configured: set a password and/or a private key.");

        return new ConnectionInfo(options.Host.Trim(), options.Port, options.Username.Trim(), [.. methods]);
    }
}
