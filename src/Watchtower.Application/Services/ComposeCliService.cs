using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>Result of <see cref="ComposeCliService.ConfigJsonAsync"/>.</summary>
/// <param name="ExitCode">Compose's exit code; non-zero means the project could not be resolved.</param>
/// <param name="Json">
/// stdout — the normalized project as JSON on success, empty on failure. Contains every resolved
/// environment value in the project, so it must never reach the deploy output or a log.
/// </param>
/// <param name="Diagnostics">stderr — Compose's warnings, or its error text when the call failed.</param>
public sealed record ComposeConfigResult(int ExitCode, string Json, string Diagnostics) {
    /// <summary>
    /// Redacted: the record's generated <c>ToString</c> would print the whole resolved project, so a
    /// single <c>Log…("{Result}", result)</c> would put every environment value the stack defines into
    /// the log. Nothing here may be logged but the exit code.
    /// </summary>
    public override string ToString() => $"{nameof(ComposeConfigResult)} {{ ExitCode = {ExitCode} }}";
}

/// <summary>
/// Runs Docker Compose CLI commands in a subprocess, capturing combined stdout/stderr output.
/// Requires docker and the compose plugin to be present in PATH inside the container.
/// </summary>
/// <remarks>
/// Not sealed, so the steps that leave the process can be stood in for and the callers that depend on
/// their success or failure — tenant teardown, the deploy pipeline — exercised without a Docker
/// daemon. Two seams do that: <see cref="DownProjectAsync"/>, whose whole contribution is an exit
/// code, and <see cref="RunAsync"/>/<see cref="RunCapturedAsync"/>, which sit below the verb methods
/// so that the arguments those verbs assemble stay under test.
/// </remarks>
public class ComposeCliService {
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
    /// <param name="overrideFilePath">Watchtower's generated override file, merged after the repository's. Null to skip.</param>
    /// <param name="onLine">Optional callback invoked for each output line as it arrives.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Exit code and captured output.</returns>
    public Task<(int ExitCode, string Output)> PullAsync(
        string composeFilePath, string projectName, string? dockerConfigDir, string? envFilePath,
        string? overrideFilePath, Action<string>? onLine, CancellationToken ct) {
        var args = BuildComposeArgs(composeFilePath, projectName, envFilePath, overrideFilePath, "pull");
        return RunAsync(args, dockerConfigDir, onLine, ct);
    }

    /// <summary>
    /// Runs <c>docker compose config --quiet</c> to validate the compose file without modifying
    /// any containers. Returns a non-zero exit code and error output if the file is invalid.
    /// </summary>
    public Task<(int ExitCode, string Output)> ConfigAsync(
        string composeFilePath, string projectName, CancellationToken ct) {
        var args = BuildComposeArgs(
            composeFilePath, projectName, envFilePath: null, overrideFilePath: null, "config", "--quiet");
        return RunAsync(args, dockerConfigDir: null, onLine: null, ct);
    }

    /// <summary>
    /// Runs <c>docker compose config --format json</c> to read back the fully resolved project — the
    /// services the deploy is about to start, and the labels declared on them.
    /// </summary>
    /// <remarks>
    /// Unlike every other call here, stdout is kept apart from stderr: the answer is a JSON document
    /// and Compose writes its warnings (deprecated attributes, unset variables) to stderr, which merged
    /// into the same buffer would leave nothing parseable behind. The same <c>--file</c>,
    /// <c>--project-name</c> and <c>--env-file</c> arguments as the deploy are passed, so what comes
    /// back is what the deploy will actually run rather than a differently-interpolated variant.
    /// </remarks>
    /// <param name="composeFilePath">Absolute path to the docker-compose.yml file.</param>
    /// <param name="projectName">Value passed to --project-name.</param>
    /// <param name="envFilePath">The deploy's generated .env, so interpolation resolves identically. Null to skip.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The exit code, the JSON document, and whatever Compose wrote to stderr.</returns>
    public Task<ComposeConfigResult> ConfigJsonAsync(
        string composeFilePath, string projectName, string? envFilePath, CancellationToken ct) {
        var args = BuildComposeArgs(
            composeFilePath, projectName, envFilePath, overrideFilePath: null, "config", "--format", "json");
        return RunCapturedAsync(args, ct);
    }

    /// <summary>Runs <c>docker compose up -d --remove-orphans</c> for the given compose file and project.</summary>
    /// <param name="composeFilePath">Absolute path to the docker-compose.yml file.</param>
    /// <param name="projectName">Value passed to --project-name.</param>
    /// <param name="dockerConfigDir">Directory containing a config.json with registry credentials. Null to use the default config.</param>
    /// <param name="envFilePath">Path to a .env file for compose variable substitution. Null to skip.</param>
    /// <param name="overrideFilePath">Watchtower's generated override file, merged after the repository's. Null to skip.</param>
    /// <param name="onLine">Optional callback invoked for each output line as it arrives.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Exit code and captured output.</returns>
    public Task<(int ExitCode, string Output)> UpAsync(
        string composeFilePath, string projectName, string? dockerConfigDir, string? envFilePath,
        string? overrideFilePath, Action<string>? onLine, CancellationToken ct) {
        var args = BuildComposeArgs(
            composeFilePath, projectName, envFilePath, overrideFilePath, "up", "-d", "--remove-orphans");
        return RunAsync(args, dockerConfigDir, onLine, ct);
    }

    /// <summary>
    /// Runs <c>docker compose down</c> for the given compose file and project.
    /// Useful for full stack teardown before re-creating containers.
    /// </summary>
    public Task<(int ExitCode, string Output)> DownAsync(
        string composeFilePath, string projectName, string? dockerConfigDir, CancellationToken ct) {
        var args = BuildComposeArgs(
            composeFilePath, projectName, envFilePath: null, overrideFilePath: null, "down");
        return RunAsync(args, dockerConfigDir, onLine: null, ct);
    }

    /// <summary>
    /// Runs <c>docker compose down --remove-orphans</c> for a project identified by name alone,
    /// optionally deleting its named volumes.
    /// </summary>
    /// <remarks>
    /// Tenant teardown has no repository checkout to point <c>--file</c> at — clones only exist for the
    /// duration of a deploy — so the project is resolved the way Compose resolves it without a file: from
    /// the <c>com.docker.compose.project</c> labels on the running containers. That also means a project
    /// with nothing running is a no-op success, which is the desired behaviour when a tenant's containers
    /// are already gone.
    /// </remarks>
    /// <param name="projectName">Value passed to --project-name; the compose project to tear down.</param>
    /// <param name="removeVolumes">When true, adds <c>--volumes</c> so the project's named volumes are deleted too.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Exit code and captured output.</returns>
    public virtual Task<(int ExitCode, string Output)> DownProjectAsync(
        string projectName, bool removeVolumes, CancellationToken ct) {
        var args = new List<string> { "compose", "--project-name", projectName, "down", "--remove-orphans" };
        if (removeVolumes) args.Add("--volumes");
        return RunAsync([.. args], dockerConfigDir: null, onLine: null, ct);
    }

    /// <summary>Runs one compose invocation, streaming its combined output.</summary>
    /// <remarks>
    /// The single point where an assembled argument list leaves the process, and therefore the seam a
    /// test double replaces. Overriding <em>this</em> rather than the verb methods is deliberate: each
    /// verb's own argument assembly — including whether it forwards the generated override file to
    /// <see cref="BuildComposeArgs"/> at all — then still runs under test, and a call site that quietly
    /// stopped passing it would show up as a missing <c>--file</c> rather than as nothing.
    /// </remarks>
    /// <param name="args">The full argument list, starting with <c>compose</c>.</param>
    /// <param name="dockerConfigDir">Value for <c>DOCKER_CONFIG</c>, or null for the default location.</param>
    /// <param name="onLine">Optional callback invoked for each output line as it arrives.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Exit code and captured output.</returns>
    protected virtual Task<(int ExitCode, string Output)> RunAsync(
        string[] args, string? dockerConfigDir, Action<string>? onLine, CancellationToken ct) =>
        RunComposeAsync(args, dockerConfigDir, _dockerApiVersion, onLine, ct);

    /// <summary>
    /// Runs one compose invocation keeping stdout apart from stderr. The seam behind
    /// <see cref="ConfigJsonAsync"/>, for the same reason as <see cref="RunAsync"/>.
    /// </summary>
    /// <param name="args">The full argument list, starting with <c>compose</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The exit code, stdout and stderr.</returns>
    protected virtual Task<ComposeConfigResult> RunCapturedAsync(string[] args, CancellationToken ct) =>
        RunComposeCapturedAsync(args, _dockerApiVersion, ct);

    /// <summary>
    /// Builds the docker compose argument list, optionally including a merged override file and
    /// <c>--env-file</c>.
    /// </summary>
    /// <remarks>
    /// Internal so the argument order can be pinned by a test: <paramref name="overrideFilePath"/> is
    /// passed as a second <c>--file</c> <em>after</em> the repository's, and Compose merges files in the
    /// order they are given, so that position is what makes the override authoritative for the keys it
    /// defines. Reversing the two would silently hand the repository the last word.
    /// </remarks>
    /// <param name="composeFilePath">Absolute path to the repository's compose file.</param>
    /// <param name="projectName">Value passed to --project-name.</param>
    /// <param name="envFilePath">Path to a .env file for compose variable substitution. Null to skip.</param>
    /// <param name="overrideFilePath">Watchtower's generated override file. Null to skip.</param>
    /// <param name="subcommandArgs">The subcommand and its own arguments.</param>
    /// <returns>The full argument list, starting with <c>compose</c>.</returns>
    internal static string[] BuildComposeArgs(
        string composeFilePath, string projectName, string? envFilePath, string? overrideFilePath,
        params string[] subcommandArgs) {
        var args = new List<string> { "compose", "--file", composeFilePath };
        if (overrideFilePath is not null) {
            args.Add("--file");
            args.Add(overrideFilePath);
        }
        args.Add("--project-name");
        args.Add(projectName);
        if (envFilePath is not null) {
            args.Add("--env-file");
            args.Add(envFilePath);
        }
        args.AddRange(subcommandArgs);
        return [.. args];
    }

    /// <summary>Builds the subprocess start info shared by every compose invocation.</summary>
    private static ProcessStartInfo CreateStartInfo(string[] args, string? dockerConfigDir, string? dockerApiVersion) {
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
        return startInfo;
    }

    /// <summary>
    /// Runs a compose invocation keeping stdout and stderr apart, for the one caller whose stdout has
    /// to stay machine-readable. Nothing is echoed to a live callback: the JSON is the resolved project
    /// and carries every value the repository defines.
    /// </summary>
    private static async Task<ComposeConfigResult> RunComposeCapturedAsync(
        string[] args, string? dockerApiVersion, CancellationToken ct) {
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        using var process = new Process {
            StartInfo = CreateStartInfo(args, dockerConfigDir: null, dockerApiVersion),
        };
        process.OutputDataReceived += (_, e) => {
            if (e.Data is not null) standardOutput.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) => {
            if (e.Data is not null) standardError.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);
        return new ComposeConfigResult(process.ExitCode, standardOutput.ToString(), standardError.ToString());
    }

    private static async Task<(int ExitCode, string Output)> RunComposeAsync(
        string[] args, string? dockerConfigDir, string? dockerApiVersion, Action<string>? onLine, CancellationToken ct) {
        var output = new StringBuilder();

        // Log the exact command so users can see what is being run.
        var commandLine = $"$ docker {string.Join(' ', args)}";
        output.AppendLine(commandLine);
        onLine?.Invoke(commandLine);

        using var process = new Process { StartInfo = CreateStartInfo(args, dockerConfigDir, dockerApiVersion) };

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
