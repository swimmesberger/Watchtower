using System.Diagnostics;
using System.Text;

namespace Watchtower.Application.Services;

/// <summary>
/// Clones git repositories into temporary directories using the git CLI.
/// Each deploy clones fresh to ensure the latest commit is used; the temp
/// directory is the caller's responsibility to delete after use.
/// </summary>
/// <remarks>
/// Not sealed, and the two calls a deploy makes are virtual, so the deploy pipeline can be exercised
/// against a checkout that never existed: every stack in a test names a repository that is not there,
/// and a clone is the first thing a deploy does.
/// </remarks>
public class GitCloneService {
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
    public virtual Task<(int ExitCode, string Output)> CloneAsync(
        string repositoryUrl, string branch, string? token, string targetDir,
        Action<string>? onLine, CancellationToken ct) {
        var authenticatedUrl = token is null ? repositoryUrl : EmbedToken(repositoryUrl, token);
        return RunGitAsync(["clone", "--depth", "1", "--branch", branch, authenticatedUrl, targetDir], onLine, ct);
    }

    /// <summary>
    /// Clones <paramref name="repositoryUrl"/> at exactly <paramref name="commitSha"/> into
    /// <paramref name="targetDir"/> — the checkout a release-pinned deploy needs
    /// (docs/products/design.md, "Clone at a commit").
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>git clone --depth 1 --branch</c> cannot check out an arbitrary commit and <c>--revision</c>
    /// needs git ≥ 2.49, so the portable form is <c>init</c> + a shallow <c>fetch</c> of the one commit
    /// + <c>checkout FETCH_HEAD</c>. The resulting detached HEAD is fine: <see cref="GetHeadCommitAsync"/>
    /// still reads the commit back. On that path the URL is handed to <c>fetch</c> rather than stored
    /// with <c>remote add</c>, so an embedded token stays in argv and never reaches <c>.git/config</c>.
    /// </para>
    /// <para>
    /// Fetching a commit by SHA needs <c>uploadpack.allowReachableSHA1InWant</c>, which GitHub, GitLab
    /// and Gitea enable but a plain self-hosted remote does not. A failed fetch is not fatal: the
    /// fallback is a full (non-shallow) clone of <paramref name="branch"/> followed by a checkout of the
    /// commit — slower, but correct anywhere — and it announces itself in the deploy output rather than
    /// silently costing a minute. The fallback is an ordinary <c>clone</c>, so it records the
    /// authenticated URL in <c>remote.origin.url</c> exactly as <see cref="CloneAsync"/> does; the same
    /// pre-existing exposure, in a temp directory the deploy deletes on its way out.
    /// </para>
    /// </remarks>
    /// <param name="repositoryUrl">HTTPS repository URL (without embedded credentials).</param>
    /// <param name="branch">Branch the commit is expected on; only the fallback clone needs it.</param>
    /// <param name="commitSha">Full 40-character commit SHA to check out.</param>
    /// <param name="token">Token to embed in the URL for authentication. Pass null for public repositories.</param>
    /// <param name="targetDir">Absolute path of the directory to clone into (must not exist yet).</param>
    /// <param name="onLine">Optional callback invoked for each output line as it arrives.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Exit code and captured output from every git command that ran.</returns>
    public virtual async Task<(int ExitCode, string Output)> CloneAtCommitAsync(
        string repositoryUrl, string branch, string commitSha, string? token, string targetDir,
        Action<string>? onLine, CancellationToken ct) {
        var output = new StringBuilder();
        void Emit(string line) {
            output.AppendLine(line);
            onLine?.Invoke(line);
        }

        // Validated before anything is shelled out: the SHA reaches git's argument list and later a
        // FETCH_HEAD checkout, and "not a commit" is a clearer failure than whatever git makes of it.
        if (!IsCommitSha(commitSha)) {
            Emit($"[Watchtower] '{commitSha}' is not a 40-character commit SHA.");
            return (InvalidArgumentExitCode, output.ToString());
        }

        var authenticatedUrl = token is null ? repositoryUrl : EmbedToken(repositoryUrl, token);

        var init = await RunGitAsync(["init", targetDir], onLine, ct);
        output.Append(init.Output);
        if (init.ExitCode != 0) return (init.ExitCode, output.ToString());

        var fetch = await RunGitAsync(
            ["-C", targetDir, "fetch", "--depth", "1", authenticatedUrl, commitSha], onLine, ct);
        output.Append(fetch.Output);
        if (fetch.ExitCode == 0) {
            var checkout = await RunGitAsync(["-C", targetDir, "checkout", "FETCH_HEAD"], onLine, ct);
            output.Append(checkout.Output);
            return (checkout.ExitCode, output.ToString());
        }

        // Deliberately hedged: the most likely reason is a remote that does not allow fetching an
        // arbitrary commit (uploadpack.allowReachableSHA1InWant), but authentication, DNS and a commit
        // that simply is not there all land here too, and git's own lines above say which.
        Emit(
            $"[Watchtower] Warning: could not fetch commit {commitSha[..8]} directly (the remote may not "
            + $"allow fetching an arbitrary commit); falling back to a full clone of branch '{branch}', "
            + "which is slower.");

        // `git clone` refuses a destination that is not empty, and the init above left a .git there.
        SafeDelete(targetDir);
        var fullClone = await RunGitAsync(["clone", "--branch", branch, authenticatedUrl, targetDir], onLine, ct);
        output.Append(fullClone.Output);
        if (fullClone.ExitCode != 0) return (fullClone.ExitCode, output.ToString());

        var checkoutSha = await RunGitAsync(["-C", targetDir, "checkout", commitSha], onLine, ct);
        output.Append(checkoutSha.Output);
        return (checkoutSha.ExitCode, output.ToString());
    }

    /// <summary>Exit code reported for a request this service rejected before running git.</summary>
    private const int InvalidArgumentExitCode = 128;

    /// <summary>True when <paramref name="value"/> is a full 40-character hexadecimal commit SHA.</summary>
    private static bool IsCommitSha(string? value) =>
        value is { Length: 40 } && value.All(Uri.IsHexDigit);

    /// <summary>Removes a directory this service created; best-effort, since the next step reports the failure.</summary>
    private static void SafeDelete(string path) {
        try {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The clone below will fail on the non-empty destination and say so in the deploy output.
        }
    }

    /// <summary>
    /// Resolves the commit SHA at the head of <paramref name="branch"/> on the remote without cloning
    /// (via <c>git ls-remote</c>). Returns null when the branch doesn't exist or the remote is unreachable.
    /// </summary>
    /// <remarks>
    /// Virtual like the two clones: it is the third call this service makes off the machine, and
    /// "release mode does not poll the branch for a pinned stack" is only checkable if a test can see
    /// whether it happened.
    /// </remarks>
    public virtual async Task<string?> GetRemoteHeadAsync(
        string repositoryUrl, string branch, string? token, CancellationToken ct) {
        var authenticatedUrl = token is null ? repositoryUrl : EmbedToken(repositoryUrl, token);
        var (exitCode, output) = await RunGitAsync(
            ["ls-remote", authenticatedUrl, $"refs/heads/{branch}"], onLine: null, ct);
        if (exitCode != 0) return null;
        // Output shape: "<sha>\trefs/heads/<branch>\n" — empty when the branch doesn't exist.
        var sha = output.Split('\t', '\n')[0].Trim();
        return sha.Length == 40 ? sha : null;
    }

    /// <summary>Returns the checked-out HEAD commit SHA of a local clone, or null when it can't be read.</summary>
    public virtual async Task<string?> GetHeadCommitAsync(string repoDir, CancellationToken ct) {
        var (exitCode, output) = await RunGitAsync(["-C", repoDir, "rev-parse", "HEAD"], onLine: null, ct);
        if (exitCode != 0) return null;
        var sha = output.Trim();
        return sha.Length == 40 ? sha : null;
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
