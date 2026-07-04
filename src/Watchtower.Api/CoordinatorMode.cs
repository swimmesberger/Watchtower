using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Api;

/// <summary>
/// Entry point for coordinator mode (<c>--self-update</c> CLI flag).
/// </summary>
/// <remarks>
/// When Watchtower needs to update itself, it cannot run <c>docker compose up -d</c> from within its
/// own container — Docker would kill the process mid-execution by terminating the container. Instead it
/// spawns a sibling container (same image, same Docker socket) that runs in this mode: wait briefly for
/// the original container to finish its HTTP response, then trigger the compose re-deploy, then exit.
/// </remarks>
internal static class CoordinatorMode {
    private const string Flag = "--self-update";

    /// <summary>Returns true when the process was launched in coordinator mode.</summary>
    internal static bool IsApplicable(string[] args) => args.Contains(Flag);

    /// <summary>Runs the coordinator and exits the process. Never returns.</summary>
    internal static async Task RunAndExitAsync(string[] args) {
        var composeFile = GetArg(args, "--compose-file")
            ?? throw new InvalidOperationException("--compose-file is required in coordinator mode");
        var projectName = GetArg(args, "--project-name")
            ?? throw new InvalidOperationException("--project-name is required in coordinator mode");

        // Allow the triggering container to finish returning its response before compose stops and
        // recreates it. 3 seconds is more than enough.
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Reuse the same DockerApiVersion as the main process (passed via env var by SelfUpdateService).
        var apiVersion = Environment.GetEnvironmentVariable("WATCHTOWER__DOCKERAPIVERSION") ?? "1.43";
        var compose = new ComposeCliService(Options.Create(new WatchtowerOptions { DockerApiVersion = apiVersion }));

        // The image was already pulled by the main process before spawning this coordinator,
        // so only the compose up -d restart step is needed here.
        var (exitCode, output) = await compose.UpAsync(
            composeFile, projectName, dockerConfigDir: null, envFilePath: null, onLine: null, CancellationToken.None);

        Console.WriteLine(output);
        Environment.Exit(exitCode);
    }

    private static string? GetArg(string[] args, string name) {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
