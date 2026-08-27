using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Api;

/// <summary>
/// Entry point for restore-coordinator mode (<c>--restore-self</c>, ADR-0027 §5).
/// </summary>
/// <remarks>
/// <para>
/// Watchtower cannot replay a dump over its own database: <c>pg_dumpall --clean</c> terminates every
/// session and drops every database, and Watchtower's connection pool would reconnect into the middle of
/// that. So it spawns a sibling container (same image, same Docker socket) running in this mode, which
/// stops Watchtower, replays, and starts it again.
/// </para>
/// <para>
/// It <b>stops and starts</b> the container rather than recreating it, unlike the self-update
/// coordinator. That is deliberate: the container's filesystem survives, and with it the marker file the
/// restarted Watchtower reads to find out what happened here.
/// </para>
/// <para>
/// The dump is already inside the database container when this starts — placed there by the process
/// that spawned this one, which had the archive and its passphrase. This mode only replays.
/// </para>
/// </remarks>
internal static class RestoreCoordinatorMode {
    /// <summary>
    /// What the database is called in this mode's output. The dump/replay code names a compose service;
    /// here there is only ever one database, and it is Watchtower's own.
    /// </summary>
    private const string Service = "watchtower";

    /// <summary>Returns true when the process was launched in restore-coordinator mode.</summary>
    internal static bool IsApplicable(string[] args) =>
        args.Contains(RestoreCoordinatorEnvironment.Flag);

    /// <summary>Runs the restore and exits the process. Never returns.</summary>
    internal static async Task RunAndExitAsync(string[] args) {
        var watchtowerId = Required(args, "--container-id");
        var postgresId = Required(args, "--postgres-id");
        var sqlPath = Required(args, "--sql");
        var user = Required(args, "--db-user");
        var execUser = GetArg(args, "--db-exec-user");
        var expected = GetAll(args, "--expect-db");
        var password = Environment.GetEnvironmentVariable(
            RestoreCoordinatorEnvironment.PostgresPassword);

        var apiVersion = Environment.GetEnvironmentVariable("WATCHTOWER__DOCKERAPIVERSION") ?? "1.43";
        using var docker = new DockerEngineClient(
            Options.Create(new WatchtowerOptions { DockerApiVersion = apiVersion }));
        var ct = CancellationToken.None;
        var connection = new PostgresConnection(user, password, execUser) { Databases = expected };
        // The shipped dump/replay implementation, not a second one: this mode has no DI and no logging
        // sink, which is all the null logger costs it.
        var postgres = new PostgresDumpService(docker, NullLogger<PostgresDumpService>.Instance);
        void Log(string line) => Console.WriteLine(line);

        // Let the request that started this return before its container is stopped.
        await Task.Delay(TimeSpan.FromSeconds(3), ct);

        // A dump of what is there now, taken before anything is dropped. It turns the worst failure
        // mode — a half-replayed --clean script — into something recoverable.
        Console.WriteLine("Taking a safety dump of the current database…");
        try {
            await postgres.DumpToContainerFileAsync(
                postgresId, connection, RestoreCoordinatorEnvironment.SafetyDumpPath, ct);
        } catch (Exception ex) {
            // Nothing has been touched yet, so the safe thing is to stop here rather than replay
            // without a way back.
            Console.WriteLine($"Could not take a safety dump: {ex.Message}");
            Console.WriteLine("Nothing was changed — Watchtower is still running on its own database.");
            Environment.Exit(1);
        }

        Console.WriteLine($"Stopping Watchtower ({Short(watchtowerId)}) for the replay…");
        await docker.StopContainerAsync(watchtowerId, ct);

        var restored = false;
        try {
            await postgres.WaitReadyAsync(postgresId, connection, Service, Log, ct);
            Console.WriteLine("Replaying the dump…");
            await postgres.ReplayRemoteAsync(postgresId, connection, Service, sqlPath, expected, Log, ct);
            restored = true;
            Console.WriteLine("Replay complete.");
        } catch (Exception ex) {
            Console.WriteLine($"Replay failed: {ex.Message}");
            Console.WriteLine("Rolling back to the safety dump.");
            try {
                // No expected-database check on the way back: the safety dump is whatever was there, and
                // the point is to restore it rather than to assert what it held.
                await postgres.ReplayRemoteAsync(
                    postgresId, connection, Service, RestoreCoordinatorEnvironment.SafetyDumpPath,
                    expectedDatabases: [], Log, ct);
                Console.WriteLine("Rollback complete — the database is as it was before the restore.");
            } catch (Exception rollbackEx) {
                Console.WriteLine(
                    $"Rollback failed too: {rollbackEx.Message}. The database may be in a partial state; "
                    + $"the pre-restore dump is at {RestoreCoordinatorEnvironment.SafetyDumpPath} inside "
                    + "the database container.");
            }
        } finally {
            // Always, whatever happened: an instance that is down is worse than one that is unchanged.
            Console.WriteLine($"Starting Watchtower ({Short(watchtowerId)}) again…");
            try {
                await docker.StartContainerAsync(watchtowerId, ct);
            } catch (Exception ex) {
                Console.WriteLine(
                    $"Could not start Watchtower again: {ex.Message}. Start container {watchtowerId} by hand.");
            }
        }

        Console.WriteLine(restored
            ? "Instance restore complete — Watchtower is coming up on the restored database."
            : "Instance restore failed — Watchtower is coming up on the database it had.");
        Environment.Exit(restored ? 0 : 1);
    }

    private static string Short(string id) => id.Length >= 12 ? id[..12] : id;

    private static string Required(string[] args, string name) =>
        GetArg(args, name)
        ?? throw new InvalidOperationException($"{name} is required in restore-coordinator mode");

    private static string? GetArg(string[] args, string name) {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Every value of a repeatable flag, in order.</summary>
    private static string[] GetAll(string[] args, string name) => [
        .. args.Index()
            .Where(x => x.Item == name && x.Index + 1 < args.Length)
            .Select(x => args[x.Index + 1]),
    ];
}
