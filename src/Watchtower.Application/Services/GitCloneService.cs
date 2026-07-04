using System.Diagnostics;
using System.Text;

namespace Watchtower.Application.Services;

/// <summary>
/// Clones git repositories into temporary directories using the git CLI.
/// Each deploy clones fresh to ensure the latest commit is used; the temp
/// directory is the caller's responsibility to delete after use.
/// </summary>
public sealed class GitCloneService {
    /// <summary>
    /// Clones <paramref name="repositoryUrl"/> at <paramref name="branch"/> into <paramref name="targetDir"/>.
    /// Uses a depth-1 shallow clone to minimise bandwidth.
    /// </summary>
    /// <param name="repositoryUrl">HTTPS repository URL (without embedded credentials).</param>
    /// <param name="branch">Branch to clone.</param>
    /// <param name="token">Token to embed in the URL for authentication. Pass null for public repositories.</param>
    /// <param name="targetDir">Absolute path of the directory to clone into (must not exist yet).</param>
    /// <param name="onLine">Optional callback invoked for each output line as it arrives.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Exit code and captured output from git.</returns>
    public Task<(int ExitCode, string Output)> CloneAsync(
        string repositoryUrl, string branch, string? token, string targetDir,
        Action<string>? onLine, CancellationToken ct) {
        var authenticatedUrl = token is null ? repositoryUrl : EmbedToken(repositoryUrl, token);
        return RunGitAsync(["clone", "--depth", "1", "--branch", branch, authenticatedUrl, targetDir], onLine, ct);
    }

    /// <summary>
    /// Embeds a token into an HTTPS URL for authenticated git operations.
    /// E.g. https://github.com/owner/repo → https://{token}@github.com/owner/repo
    /// </summary>
    private static string EmbedToken(string repositoryUrl, string token) {
        // Use Uri to safely insert credentials without string-mangling the URL.
        var uri = new Uri(repositoryUrl);
        return new UriBuilder(uri) { UserName = token, Password = "" }.Uri
            .ToString()
            // Remove the trailing colon left by the empty password.
            .Replace($"{token}:@", $"{token}@");
    }

    private static async Task<(int ExitCode, string Output)> RunGitAsync(string[] args, Action<string>? onLine, CancellationToken ct) {
        var output = new StringBuilder();

        var startInfo = new ProcessStartInfo("git") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Suppress interactive prompts — fail instead of blocking the deploy.
            Environment = {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_ASKPASS"] = "echo",
            },
        };
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = new Process();
        process.StartInfo = startInfo;
        process.OutputDataReceived += (_, e) => {
            if (e.Data is null) return;
            output.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) => {
            if (e.Data is null) return;
            output.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);
        return (process.ExitCode, output.ToString());
    }
}
