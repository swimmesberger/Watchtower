namespace Watchtower.Application.Services;

/// <summary>
/// The contract between the Watchtower process that starts a restore and the coordinator container that
/// carries it out (ADR-0027 §5). Named in one place so the two halves cannot drift: they are compiled
/// into the same image but never run in the same process.
/// </summary>
/// <remarks>
/// <code>
/// --restore-self
/// --container-id  &lt;watchtower container&gt;   the container to stop, replay behind, and start again
/// --postgres-id   &lt;database container&gt;     the container psql is exec'd in
/// --sql           &lt;path inside it&gt;         the dump, already placed there by the starting process
/// --db-user       &lt;role&gt;                   the role that answered the preflight
/// [--db-exec-user &lt;os user&gt;]               the OS user psql must run as, when it is not the default
/// [--expect-db    &lt;name&gt;]…                 the databases the dump promises; the success check
/// </code>
/// The password, when the image needs one, travels as an environment variable on the coordinator's
/// create body — visible in <c>docker inspect</c>, which is accepted because reading it requires the
/// Docker socket, and holding that already means owning the host.
/// </remarks>
public static class RestoreCoordinatorEnvironment {
    /// <summary>The CLI flag that puts the process into restore-coordinator mode.</summary>
    public const string Flag = "--restore-self";

    /// <summary>Environment variable carrying <c>PGPASSWORD</c> to the coordinator.</summary>
    public const string PostgresPassword = "WATCHTOWER_RESTORE_PGPASSWORD";

    /// <summary>
    /// Where the coordinator writes its safety dump inside the database container, before it replaces
    /// anything. Replayed back if the restore's own replay fails.
    /// </summary>
    public const string SafetyDumpPath = "/tmp/watchtower-restore/pre-restore.sql";
}
