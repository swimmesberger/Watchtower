using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// Runs Docker Compose CLI commands in a subprocess, capturing combined stdout/stderr output.
/// Requires docker and the compose plugin to be present in PATH inside the container.
/// </summary>
public sealed class ComposeCliService {
    private readonly string? _dockerApiVersion;

    /// <param name="options">Watchtower options — reads <c>DockerApiVersion</c>
    /// and passes it as <c>DOCKER_API_VERSION</c> to compose subprocesses when set.
    /// This is needed when the docker compose CLI negotiates a newer API version than the
    /// daemon supports (e.g. compose uses 1.53 but the daemon only supports 1.43).</param>
    public ComposeCliService(IOptions<WatchtowerOptions> options) {
        _dockerApiVersion = options.Value.DockerApiVersion;
    }

    /// <summary>Runs <c>docker compose pull</c> for the given compose file and project.</summary>
    /// <param name="composeFilePath">Absolute path to the docker-compose.yml file.</param>
    /// <param name="projectName">Value passed to --project-name.</param>
    /// <param name="dockerConfigDir">Directory containing a config.json with registry credentials. Null to use the default config.</param>
    /// <param name="envFilePath">Path to a .env file for compose variable substitution. Null to skip.</param>
    /// <param name="onLine">Optional callback invoked for each output line as it arrives.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Exit code and captured output.</returns>
    public Task<(int ExitCode, string Output)> PullAsync(
        string composeFilePath, string projectName, string? dockerConfigDir, string? envFilePath,
        Action<string>? onLine, CancellationToken ct) {
        var args = BuildArgs(composeFilePath, projectName, envFilePath, "pull");
        return RunComposeAsync(args, dockerConfigDir, _dockerApiVersion, onLine, ct);
    }

    /// <summary>
    /// Runs <c>docker compose config --quiet</c> to validate the compose file without modifying
    /// any containers. Returns a non-zero exit code and error output if the file is invalid.
    /// </summary>
    public Task<(int ExitCode, string Output)> ConfigAsync(
        string composeFilePath, string projectName, CancellationToken ct) {
        var args = BuildArgs(composeFilePath, projectName, envFilePath: null, "config", "--quiet");
        return RunComposeAsync(args, dockerConfigDir: null, _dockerApiVersion, onLine: null, ct);
    }

    /// <summary>Runs <c>docker compose up -d --remove-orphans</c> for the given compose file and project.</summary>
    public Task<(int ExitCode, string Output)> UpAsync(
        string composeFilePath, string projectName, string? dockerConfigDir, string? envFilePath,
        Action<string>? onLine, CancellationToken ct) {
        var args = BuildArgs(composeFilePath, projectName, envFilePath, "up", "-d", "--remove-orphans");
        return RunComposeAsync(args, dockerConfigDir, _dockerApiVersion, onLine, ct);
    }

    /// <summary>
    /// Runs <c>docker compose down</c> for the given compose file and project.
    /// Useful for full stack teardown before re-creating containers.
    /// </summary>
    public Task<(int ExitCode, string Output)> DownAsync(
        string composeFilePath, string projectName, string? dockerConfigDir, CancellationToken ct) {
        var args = BuildArgs(composeFilePath, projectName, envFilePath: null, "down");
        return RunComposeAsync(args, dockerConfigDir, _dockerApiVersion, onLine: null, ct);
    }

    /// <summary>Builds the docker compose argument list, optionally including --env-file.</summary>
    private static string[] BuildArgs(string composeFilePath, string projectName, string? envFilePath, params string[] subcommandArgs) {
        var args = new List<string> { "compose", "--file", composeFilePath, "--project-name", projectName };
        if (envFilePath is not null) {
            args.Add("--env-file");
            args.Add(envFilePath);
        }
        args.AddRange(subcommandArgs);
        return [.. args];
    }

    private static async Task<(int ExitCode, string Output)> RunComposeAsync(
        string[] args, string? dockerConfigDir, string? dockerApiVersion, Action<string>? onLine, CancellationToken ct) {
        var output = new StringBuilder();

        // Log the exact command so users can see what is being run.
        var commandLine = $"$ docker {string.Join(' ', args)}";
        output.AppendLine(commandLine);
        onLine?.Invoke(commandLine);

        var startInfo = new ProcessStartInfo("docker") {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // DOCKER_CONFIG scopes registry credentials to this subprocess only when specified.
        // When null, the default Docker config location is used.
        if (dockerConfigDir is not null)
            startInfo.Environment["DOCKER_CONFIG"] = dockerConfigDir;
        // DOCKER_API_VERSION pins the API version used by the compose CLI.
        // Required when the compose client negotiates a newer version than the daemon supports.
        if (!string.IsNullOrEmpty(dockerApiVersion))
            startInfo.Environment["DOCKER_API_VERSION"] = dockerApiVersion;
        foreach (var arg in args) startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };

        // Capture stdout and stderr into the same buffer for a unified log.
        // Also invoke onLine immediately so SSE subscribers see output as it arrives.
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
