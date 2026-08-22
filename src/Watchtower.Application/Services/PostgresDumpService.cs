using System.Text;
using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services;

/// <summary>
/// How to reach the PostgreSQL server inside one container: the role to connect as, its password (if
/// the image was given one) and the OS user the exec has to run as.
/// </summary>
/// <param name="User">The database role, from <c>POSTGRES_USER</c>, defaulting to <c>postgres</c>.</param>
/// <param name="Password">
/// <c>POSTGRES_PASSWORD</c>, or null when the image runs with trust/peer authentication. Travels to
/// the daemon as a <c>PGPASSWORD</c> exec environment variable and nowhere else.
/// </param>
/// <param name="ExecUser">
/// The OS user to run <c>psql</c>/<c>pg_dumpall</c> as, or null for the image's default. Set to
/// <c>postgres</c> when the default user could not authenticate, which is what peer authentication on
/// the local socket requires.
/// </param>
public sealed record PostgresConnection(string User, string? Password, string? ExecUser) {
    /// <summary>
    /// The databases the preflight probe found, sorted. Carried on the connection so the manifest can
    /// record what the dump covers without a second round-trip into the container.
    /// </summary>
    public IReadOnlyList<string> Databases { get; init; } = [];

    /// <summary>
    /// Redacted: the record's generated <c>ToString</c> would print the database password, so a single
    /// <c>Log…("{Connection}", connection)</c> would put it in the log. Nothing here may be shown but
    /// the two user names.
    /// </summary>
    public override string ToString() =>
        $"{nameof(PostgresConnection)} {{ User = {User}, ExecUser = {ExecUser ?? "(image default)"}, "
        + $"Password = {(string.IsNullOrEmpty(Password) ? "(none)" : "***")} }}";
}

/// <summary>What one completed dump produced.</summary>
/// <param name="SizeBytes">Size of the spooled SQL file.</param>
/// <param name="Databases">The databases the dump covers, as the preflight probe listed them.</param>
public sealed record PostgresDumpResult(long SizeBytes, IReadOnlyList<string> Databases);

/// <summary>
/// Takes a logical dump of a PostgreSQL container with <c>pg_dumpall</c>, over the engine's exec API
/// (ADR-0017). A dump is consistent by construction, so the database keeps serving traffic through the
/// backup instead of being stopped for a file-level snapshot of its data directory — and the archive
/// carries SQL that any Postgres of a compatible version can replay, rather than a page image that
/// only the same major version can open.
/// </summary>
/// <remarks>
/// Nothing here logs a password: it reaches the container as a <c>PGPASSWORD</c> exec environment
/// variable, which <see cref="DockerEngineClient.ExecAsync"/> hands to the daemon and never echoes,
/// and <see cref="PostgresConnection.ToString"/> redacts it.
/// </remarks>
public sealed class PostgresDumpService(DockerEngineClient docker, ILogger<PostgresDumpService> logger) {
    /// <summary>How long a restarted database may take to accept connections before a replay gives up.</summary>
    internal static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Gap between readiness probes while waiting out <see cref="ReadyTimeout"/>.</summary>
    internal static readonly TimeSpan ReadyPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Client-side ceiling for one <c>pg_dumpall</c>. The exec runs on the untimed HTTP client — a real
    /// dump takes minutes, not seconds — so without this a wedged server would park the backup worker
    /// forever. Generous on purpose: a large database is legitimately slow, and exceeding this is
    /// reported as a failure rather than an empty archive.
    /// </summary>
    internal static readonly TimeSpan DumpTimeout = TimeSpan.FromHours(2);

    /// <summary>Directory inside the archive the dumps live in, next to the volume directories.</summary>
    internal const string DumpDirectory = "_dumps";

    /// <summary>The databases worth dumping: no templates, and nothing that refuses connections.</summary>
    private const string DatabaseQuery =
        "select datname from pg_database where not datistemplate and datallowconn";

    /// <summary>How much of a failure's stderr goes into the exception message.</summary>
    private const int MessageTailChars = 1000;

    /// <summary>
    /// Works out how to talk to the database and proves it works, <b>before</b> the run stops anything.
    /// A stack whose database cannot be dumped has to fail while it is still fully up: the alternative
    /// is discovering it after the stop step, with the archive half-written.
    /// </summary>
    /// <param name="target">The container to dump.</param>
    /// <param name="log">Receives operator-facing lines, <c>WARNING: </c> prefix included.</param>
    /// <param name="ct">The run's token.</param>
    /// <returns>The connection the dump is to use, carrying the databases it will cover.</returns>
    /// <exception cref="InvalidOperationException">Neither the image's default user nor <c>postgres</c> could connect.</exception>
    public async Task<PostgresConnection> PreflightAsync(
        DumpTarget target, Action<string> log, CancellationToken ct) {
        var details = await docker.InspectContainerAsync(target.ContainerId, ct);
        var env = details.Config.Env ?? [];
        var user = EnvValue(env, "POSTGRES_USER") ?? "postgres";
        var password = EnvValue(env, "POSTGRES_PASSWORD");
        var execEnv = ExecEnv(password);

        PostgresConnection? connection = null;
        DockerExecResult? failure = null;
        // The image's own default user first; then postgres, which is what the local socket's peer
        // authentication needs when the entrypoint left the default as root.
        string?[] execUsers = [null, "postgres"];
        foreach (var execUser in execUsers) {
            var probe = await docker.ExecAsync(
                target.ContainerId, Psql(user, "select 1"), stdout: null, execEnv, execUser, ct);
            if (probe.Success) {
                connection = new PostgresConnection(user, password, execUser);
                break;
            }
            failure = probe;
        }
        if (connection is null)
            throw new InvalidOperationException(
                $"Could not connect to the database of service '{target.Service}' as role '{user}' "
                + $"(exit code {failure?.ExitCode}): {Tail(failure?.Stderr)}");

        using var stdout = new MemoryStream();
        var listing = await docker.ExecAsync(
            target.ContainerId, Psql(user, DatabaseQuery), stdout, execEnv, connection.ExecUser, ct);
        if (!listing.Success)
            throw new InvalidOperationException(
                $"Could not list the databases of service '{target.Service}' "
                + $"(exit code {listing.ExitCode}): {Tail(listing.Stderr)}");

        var databases = ParseDatabases(stdout.ToArray());
        logger.LogInformation(
            "Postgres preflight for container {ContainerId} succeeded: {DatabaseCount} database(s)",
            target.ContainerId, databases.Count);
        log($"Service '{target.Service}' answers as role '{connection.User}' "
            + $"— {databases.Count} database(s) to dump: {string.Join(", ", databases)}");
        return connection with { Databases = databases };
    }

    /// <summary>
    /// Runs <c>pg_dumpall</c> and spools its stdout to <paramref name="spoolPath"/>. Uncompressed: the
    /// archive is gzipped as a whole downstream, and compressing twice would only cost CPU.
    /// </summary>
    /// <remarks>
    /// A non-zero exit fails the whole run rather than falling back to a file snapshot of a database
    /// that is still up. Such a snapshot would be hot and torn, and it would look exactly like a good
    /// one; yesterday's archive is still on storage, which is the better thing to restore from.
    /// <c>--clean --if-exists</c> makes the dump replayable into a populated server, and role passwords
    /// are deliberately kept (no <c>--no-role-passwords</c>) so a restore does not lock every user out.
    /// </remarks>
    /// <param name="target">The container to dump.</param>
    /// <param name="connection">What <see cref="PreflightAsync"/> established.</param>
    /// <param name="spoolPath">Host path for the SQL file; created (and overwritten).</param>
    /// <param name="log">Receives operator-facing lines, <c>WARNING: </c> prefix included.</param>
    /// <param name="ct">The run's token.</param>
    /// <exception cref="TimeoutException">The dump outlasted <see cref="DumpTimeout"/>.</exception>
    /// <exception cref="InvalidOperationException"><c>pg_dumpall</c> exited non-zero.</exception>
    public async Task<PostgresDumpResult> DumpAsync(
        DumpTarget target, PostgresConnection connection, string spoolPath,
        Action<string> log, CancellationToken ct) {
        // The cap gets its own source rather than a CancelAfter on the linked one, so "the cap fired"
        // is a fact to read off `cap` instead of something inferred from the caller's token.
        using var cap = new CancellationTokenSource(DumpTimeout);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, cap.Token);
        DockerExecResult result;
        try {
            await using var spool = File.Create(spoolPath);
            result = await docker.ExecAsync(
                target.ContainerId,
                ["pg_dumpall", $"--username={connection.User}", "--clean", "--if-exists", "--no-password"],
                spool, ExecEnv(connection.Password), connection.ExecUser, bounded.Token);
        } catch (OperationCanceledException) when (cap.IsCancellationRequested) {
            throw new TimeoutException(
                $"The dump of service '{target.Service}' exceeded the client-side cap of {DumpTimeout}. "
                + "The server may still be carrying it through.");
        }

        if (!result.Success)
            throw new InvalidOperationException(
                $"pg_dumpall for service '{target.Service}' failed with exit code {result.ExitCode}: "
                + Tail(result.Stderr));

        var sizeBytes = new FileInfo(spoolPath).Length;
        var diagnostics = SplitLines(result.Stderr);
        if (diagnostics.Count > 0) {
            // Exit 0 with output on stderr is usually a NOTICE, occasionally a permission the dump
            // silently skipped — worth surfacing, never worth failing on.
            log($"WARNING: pg_dumpall for '{target.Service}' wrote {diagnostics.Count} line(s) to stderr: "
                + string.Join(" | ", diagnostics.Take(3)));
            logger.LogWarning(
                "pg_dumpall for container {ContainerId} wrote {LineCount} line(s) to stderr",
                target.ContainerId, diagnostics.Count);
        }
        log($"Dump of '{target.Service}' complete: {sizeBytes} bytes, {connection.Databases.Count} database(s).");
        return new PostgresDumpResult(sizeBytes, connection.Databases);
    }

    /// <summary>
    /// A non-interactive <c>psql</c> invocation returning one bare column: <c>-w</c> so a missing
    /// password fails instead of waiting on a prompt nobody can answer, <c>-tAc</c> so the output is
    /// the values alone.
    /// </summary>
    private static string[] Psql(string user, string sql) =>
        ["psql", "-U", user, "-d", "postgres", "-w", "-tAc", sql];

    /// <summary>The exec environment carrying the password, or null when there is none to carry.</summary>
    private static string[]? ExecEnv(string? password) =>
        string.IsNullOrEmpty(password) ? null : [$"PGPASSWORD={password}"];

    /// <summary>Reads <paramref name="key"/> out of a container's <c>KEY=VALUE</c> environment.</summary>
    private static string? EnvValue(string[] env, string key) {
        var prefix = key + "=";
        foreach (var entry in env)
            if (entry.StartsWith(prefix, StringComparison.Ordinal)) return entry[prefix.Length..];
        return null;
    }

    /// <summary>The database names psql printed, sorted so the manifest does not churn between runs.</summary>
    private static IReadOnlyList<string> ParseDatabases(byte[] stdout) {
        var names = SplitLines(Encoding.UTF8.GetString(stdout));
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    /// <summary>Non-empty, trimmed lines of a captured stream.</summary>
    private static List<string> SplitLines(string text) => [
        .. text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
    ];

    /// <summary>
    /// The end of a diagnostic, for an exception message that ends up in an audit row: the last
    /// <see cref="MessageTailChars"/> characters, since the reason a command failed is what it said
    /// last.
    /// </summary>
    private static string Tail(string? stderr) {
        var text = stderr?.Trim() ?? "";
        if (text.Length == 0) return "the process wrote nothing to stderr.";
        return text.Length <= MessageTailChars ? text : $"…{text[^MessageTailChars..]}";
    }
}
