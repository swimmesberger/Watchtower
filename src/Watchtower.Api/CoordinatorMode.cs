using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Api;

/// <summary>
/// Entry point for coordinator mode (<c>--self-update</c> CLI flag).
/// </summary>
/// <remarks>
/// When Watchtower needs to update itself, it cannot recreate its own container from within — the
/// process dies the moment its container stops. Instead it spawns a sibling container (same image,
/// same Docker socket) that runs in this mode: wait briefly for the original request to finish,
/// then recreate the Watchtower container via the Docker API — clone its configuration onto the
/// already-pulled new image (<see cref="ContainerCloneSpec"/>), stop and rename the old container
/// aside, create and start the replacement, and roll back to the old container if that fails. No
/// compose file is involved, so nothing about the host layout needs to be known or configured.
/// <para>
/// The same machinery publishes host ports (ADR-0033). Docker cannot add a port binding to a running
/// container either, so <c>--publish-ports</c>/<c>--unpublish-ports</c> amend the clone's bindings on
/// the way through and everything else — the delay, stop, rename-aside, create, start, rollback and
/// exit codes — is identical. Such a run passes the container's <em>current</em> image as
/// <c>--image</c>: nothing was pulled, and retargeting is not what it is for.
/// </para>
/// </remarks>
internal static class CoordinatorMode {
    private const string Flag = "--self-update";

    /// <summary>Returns true when the process was launched in coordinator mode.</summary>
    internal static bool IsApplicable(string[] args) => args.Contains(Flag);

    /// <summary>Runs the coordinator and exits the process. Never returns.</summary>
    internal static async Task RunAndExitAsync(string[] args) {
        var containerId = GetArg(args, "--container-id")
            ?? throw new InvalidOperationException("--container-id is required in coordinator mode");
        var imageRef = GetArg(args, "--image")
            ?? throw new InvalidOperationException("--image is required in coordinator mode");

        // Reuse the same DockerApiVersion as the main process (passed via env var by SelfUpdateService).
        var apiVersion = Environment.GetEnvironmentVariable("WATCHTOWER__DOCKERAPIVERSION") ?? "1.43";
        using var docker = new DockerEngineClient(Options.Create(new WatchtowerOptions { DockerApiVersion = apiVersion }));
        var ct = CancellationToken.None;

        // Tolerant on purpose, and it is PortRouteListeners.Parse that makes it so: an entry that is not
        // a port is dropped rather than thrown on. This process is the only thing standing between the
        // Watchtower container and a stopped state it cannot get out of by itself, so a malformed
        // argument must not turn into an exception between the stop and the create.
        var amendments = new ContainerCloneSpec.PortAmendments(
            PortRouteListeners.Parse(GetArg(args, "--publish-ports")),
            PortRouteListeners.Parse(GetArg(args, "--unpublish-ports")));

        var inspect = await docker.InspectContainerRawAsync(containerId, ct);
        var spec = ContainerCloneSpec.FromInspect(inspect, imageRef, amendments);

        // Allow the triggering container to finish returning its response before it is stopped.
        // 3 seconds is more than enough.
        await Task.Delay(TimeSpan.FromSeconds(3));

        Console.WriteLine(amendments.IsEmpty
            ? $"Recreating container '{spec.Name}' on image '{imageRef}'"
            : $"Recreating container '{spec.Name}' to publish [{Join(amendments.Publish)}] "
              + $"and unpublish [{Join(amendments.Unpublish)}]");
        await docker.StopContainerAsync(containerId, ct);

        // Rename the old container aside so the replacement can take its name; it stays around,
        // stopped, as the rollback target until the new container has started successfully.
        var backupName = $"{spec.Name}-previous-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        await docker.RenameContainerAsync(containerId, backupName, ct);

        string? newId = null;
        try {
            newId = await docker.CreateContainerRawAsync(spec.CreateBody, spec.Name, ct);
            foreach (var (network, endpoint) in spec.ExtraNetworks)
                await docker.ConnectNetworkAsync(network, newId, endpoint, ct);
            await docker.StartContainerAsync(newId, ct);
        } catch (Exception ex) {
            Console.WriteLine($"Recreate failed: {ex.Message}");
            Console.WriteLine("Rolling back to the previous container.");
            try {
                if (newId is not null) await docker.RemoveContainerAsync(newId, ct);
                await docker.RenameContainerAsync(containerId, spec.Name, ct);
                await docker.StartContainerAsync(containerId, ct);
                Console.WriteLine("Rollback complete — the previous version is running again.");
            } catch (Exception rollbackEx) {
                Console.WriteLine(
                    $"Rollback failed: {rollbackEx.Message}. Manual intervention required — " +
                    $"the previous container still exists as '{backupName}'.");
            }
            Environment.Exit(1);
        }

        try {
            await docker.RemoveContainerAsync(containerId, ct);
        } catch (Exception ex) {
            // The update itself succeeded; a leftover stopped container is only clutter.
            Console.WriteLine($"Warning: could not remove the previous container '{backupName}': {ex.Message}");
        }

        Console.WriteLine(amendments.IsEmpty
            ? $"Self-update complete: '{spec.Name}' is running '{imageRef}'."
            : $"Port publish complete: '{spec.Name}' was recreated with the requested host ports.");
        Environment.Exit(0);
    }

    private static string Join(IReadOnlyList<int> ports) => string.Join(", ", ports);

    private static string? GetArg(string[] args, string name) {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
