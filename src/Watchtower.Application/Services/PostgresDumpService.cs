using System.Formats.Tar;
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

/// <summary>What one replay reported.</summary>
/// <param name="ErrorLineCount">
/// How many lines psql wrote that are its own diagnostics. Never zero for a
/// <c>pg_dumpall --clean</c> script — see <see cref="PostgresReplayOutcome"/> — so it is reported,
/// not acted on.
/// </param>
/// <param name="SampleErrors">The first ten distinct diagnostic lines, for the run output.</param>
/// <param name="MissingDatabases">
/// Databases the archive promised that are not on the server afterwards. Non-empty means the restore
/// failed, whatever psql's exit code said.
/// </param>
public sealed record PostgresReplayResult(
    int ErrorLineCount, IReadOnlyList<string> SampleErrors, IReadOnlyList<string> MissingDatabases) {
    /// <summary>Why this replay is a failure, or null when it worked.</summary>
    public string? Failure { get; init; }

    /// <summary>True when every promised database is present and psql did not fail outright.</summary>
    public bool Succeeded => Failure is null;
}

/// <summary>
/// The verdict on one replay, as a pure function of what psql did (ADR-0017 §5) — kept out of
/// <see cref="PostgresDumpService"/> so the rule can be tested without a daemon.
/// </summary>
/// <remarks>
/// The rule exists because psql's exit code is not usable as a success signal here: every
/// <c>pg_dumpall --clean</c> script errors on <c>role "postgres" already exists</c>, and a script run
/// with <c>ON_ERROR_STOP=0</c> can also exit 0 having achieved nothing. So a non-zero exit is a
/// failure, and beyond that the databases themselves are the evidence: all present is a success even
/// with diagnostics, any missing is a failure even without them.
/// </remarks>
public static class PostgresReplayOutcome {
    /// <summary>How many distinct diagnostic lines are worth showing an operator.</summary>
    private const int SampleLimit = 10;

    /// <summary>
    /// psql prefixes its own diagnostics with the program name, which is not translated — unlike the
    /// server's <c>ERROR:</c>/<c>FEHLER:</c> text, which depends on the container's locale.
    /// </summary>
    private const string DiagnosticPrefix = "psql:";

    /// <summary>Judges one replay.</summary>
    /// <param name="exitCode">psql's exit code.</param>
    /// <param name="stderr">What psql wrote to stderr (already tail-bounded by the exec client).</param>
    /// <param name="expected">The databases the manifest says the dump contains.</param>
    /// <param name="present">The databases that exist on the server afterwards.</param>
    /// <returns>The counted diagnostics, the missing databases, and the failure reason if any.</returns>
    public static PostgresReplayResult Classify(
        int exitCode, string stderr, IReadOnlyList<string> expected, IReadOnlyList<string> present) {
        var diagnostics = (stderr ?? "")
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(DiagnosticPrefix, StringComparison.Ordinal))
            .ToList();
        var samples = diagnostics.Distinct(StringComparer.Ordinal).Take(SampleLimit).ToList();
        var missing = expected
            .Where(database => !present.Contains(database, StringComparer.Ordinal))
            .OrderBy(database => database, StringComparer.Ordinal)
            .ToList();

        var failure = exitCode != 0
            ? $"psql exited with code {exitCode}: {PostgresDumpService.Tail(stderr)}"
            : missing.Count > 0
                ? $"the database(s) {string.Join(", ", missing)} are not on the server afterwards — "
                    + "the dump did not restore them."
                : null;
        return new PostgresReplayResult(diagnostics.Count, samples, missing) { Failure = failure };
    }
}

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
public sealed class PostgresDumpService {
    /// <summary>How long a restarted database may take to accept connections before a replay gives up.</summary>
    internal static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(120);

    /// <summary>Gap between readiness probes while waiting out <see cref="ReadyTimeout"/>.</summary>
    internal static readonly TimeSpan ReadyPollInterval = TimeSpan.FromSeconds(1);

    private readonly DockerEngineClient _docker;
    private readonly ILogger<PostgresDumpService> _logger;
    private readonly TimeSpan _readyTimeout;
    private readonly TimeSpan _readyPollInterval;

    /// <param name="docker">The engine client every exec goes through.</param>
    /// <param name="logger">Structured log; never receives a password.</param>
    public PostgresDumpService(DockerEngineClient docker, ILogger<PostgresDumpService> logger)
        : this(docker, logger, ReadyTimeout, ReadyPollInterval) { }

    /// <summary>
    /// Test seam for the readiness wait: a test cannot spend two real minutes proving the ceiling
    /// works, and a one-second poll would make every readiness test that slow. Same shape as
    /// <see cref="DockerEngineClient"/>'s injected prune ceiling; the parameters are not resolvable
    /// from the container, so DI keeps picking the public constructor.
    /// </summary>
    internal PostgresDumpService(
        DockerEngineClient docker, ILogger<PostgresDumpService> logger,
        TimeSpan readyTimeout, TimeSpan readyPollInterval) {
        _docker = docker;
        _logger = logger;
        _readyTimeout = readyTimeout;
        _readyPollInterval = readyPollInterval;
    }

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
    /// Reads the credentials the container was started with — <c>POSTGRES_USER</c> (default
    /// <c>postgres</c>) and <c>POSTGRES_PASSWORD</c> — without contacting the server. Enough to run
    /// <c>pg_isready</c> against a database that is still starting; <see cref="PreflightAsync"/> is
    /// what proves the credentials actually work.
    /// </summary>
    /// <param name="containerId">The database container.</param>
    /// <param name="ct">The run's token.</param>
    /// <returns>The declared connection, with no exec user chosen yet.</returns>
    public async Task<PostgresConnection> ReadConnectionAsync(string containerId, CancellationToken ct) {
        var details = await _docker.InspectContainerAsync(containerId, ct);
        var env = details.Config.Env ?? [];
        return new PostgresConnection(
            EnvValue(env, "POSTGRES_USER") ?? "postgres", EnvValue(env, "POSTGRES_PASSWORD"), null);
    }

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
        var declared = await ReadConnectionAsync(target.ContainerId, ct);
        var user = declared.User;
        var password = declared.Password;
        var execEnv = ExecEnv(password);

        PostgresConnection? connection = null;
        DockerExecResult? failure = null;
        // The image's own default user first; then postgres, which is what the local socket's peer
        // authentication needs when the entrypoint left the default as root.
        string?[] execUsers = [null, "postgres"];
        foreach (var execUser in execUsers) {
            var probe = await _docker.ExecAsync(
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

        var (listing, databases) = await ListDatabasesAsync(target.ContainerId, connection, ct);
        if (!listing.Success)
            throw new InvalidOperationException(
                $"Could not list the databases of service '{target.Service}' "
                + $"(exit code {listing.ExitCode}): {Tail(listing.Stderr)}");
        _logger.LogInformation(
            "Postgres preflight for container {ContainerId} succeeded: {DatabaseCount} database(s)",
            target.ContainerId, databases.Count);
        // Neutral wording: the same probe runs on the restore path, where nothing is dumped.
        log($"Service '{target.Service}' answers as role '{connection.User}' "
            + $"— {databases.Count} database(s): {string.Join(", ", databases)}");
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
            result = await _docker.ExecAsync(
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
            _logger.LogWarning(
                "pg_dumpall for container {ContainerId} wrote {LineCount} line(s) to stderr",
                target.ContainerId, diagnostics.Count);
        }
        log($"Dump of '{target.Service}' complete: {sizeBytes} bytes, {connection.Databases.Count} database(s).");
        return new PostgresDumpResult(sizeBytes, connection.Databases);
    }

    /// <summary>
    /// Waits until the server inside <paramref name="containerId"/> accepts connections, polling
    /// <c>pg_isready</c>. Used on restore, where a database container may have been down and is
    /// started just for the replay — the container being "running" says nothing about the server
    /// inside it having finished recovery.
    /// </summary>
    /// <remarks>
    /// <c>pg_isready</c>'s exit codes are three different facts: 0 is ready, 1 (rejecting) and 2 (no
    /// response) are both "not yet", and 3 means the invocation itself was wrong — a bad argument or
    /// a container without the client tools. Polling through a 3 would just burn the ceiling on a
    /// call that can never succeed, so it fails immediately.
    /// </remarks>
    /// <param name="containerId">The database container.</param>
    /// <param name="connection">The declared credentials — only the role name is used.</param>
    /// <param name="service">The compose service, for the run output.</param>
    /// <param name="log">Receives operator-facing lines, <c>WARNING: </c> prefix included.</param>
    /// <param name="ct">The run's token.</param>
    /// <exception cref="TimeoutException">The server was still not accepting connections at the ceiling.</exception>
    /// <exception cref="InvalidOperationException"><c>pg_isready</c> could not be run at all.</exception>
    public async Task WaitReadyAsync(
        string containerId, PostgresConnection connection, string service,
        Action<string> log, CancellationToken ct) {
        var started = DateTimeOffset.UtcNow;
        var attempts = 0;
        while (true) {
            attempts++;
            var probe = await _docker.ExecAsync(
                containerId, ["pg_isready", "-U", connection.User], stdout: null,
                ExecEnv(connection.Password), connection.ExecUser, ct);
            var waited = DateTimeOffset.UtcNow - started;
            if (probe.Success) {
                log($"Waiting for postgres in '{service}' to accept connections… "
                    + $"ready after {waited.TotalSeconds:0.#}s.");
                _logger.LogInformation(
                    "Postgres in container {ContainerId} ready after {Attempts} probe(s)", containerId, attempts);
                return;
            }
            if (probe.ExitCode == 3)
                throw new InvalidOperationException(
                    $"Could not check whether postgres in '{service}' is ready — pg_isready refused the "
                    + $"call (exit code 3): {Tail(probe.Stderr)}");
            if (waited + _readyPollInterval > _readyTimeout)
                throw new TimeoutException(
                    $"Postgres in '{service}' did not accept connections within {_readyTimeout.TotalSeconds:0}s "
                    + $"(pg_isready exit code {probe.ExitCode}). The dump was not replayed.");
            await Task.Delay(_readyPollInterval, ct);
        }
    }

    /// <summary>
    /// Replays one <c>pg_dumpall</c> file into a running server: stray sessions are terminated, the
    /// SQL is copied into the container and run with <c>psql -f</c>, and the databases the archive
    /// promised are checked for afterwards.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Success is judged by the databases being there, not by psql's exit code: a
    /// <c>pg_dumpall --clean</c> script reliably errors on things that do not matter (<c>role
    /// "postgres" already exists</c> is in every single one), which is why it runs with
    /// <c>ON_ERROR_STOP=0</c> — with <c>ON_ERROR_STOP=1</c> every restore would abort on the first
    /// benign line. It is <c>-f</c> rather than <c>-c</c> because the script is a sequence of
    /// <c>\connect</c> blocks, a psql meta-command that only exists while reading a file.
    /// </para>
    /// <para>
    /// The sessions are terminated first because <c>--clean</c>'s <c>DROP DATABASE</c> fails while
    /// anything is connected, and the script would then carry on and merge the dump into the old
    /// database instead of replacing it — a restore that reports success and leaves stale rows behind.
    /// </para>
    /// </remarks>
    /// <param name="containerId">The database container, already running and ready.</param>
    /// <param name="connection">What <see cref="PreflightAsync"/> established.</param>
    /// <param name="service">The compose service, for the run output and the in-container file name.</param>
    /// <param name="sqlPath">Host path of the SQL extracted from the archive.</param>
    /// <param name="expectedDatabases">The databases the manifest says the dump contains.</param>
    /// <param name="log">Receives operator-facing lines, <c>WARNING: </c> prefix included.</param>
    /// <param name="ct">The run's token.</param>
    /// <returns>What psql reported and which expected databases are still missing.</returns>
    /// <exception cref="InvalidOperationException">psql failed, or a database did not come back.</exception>
    public async Task<PostgresReplayResult> ReplayAsync(
        string containerId, PostgresConnection connection, string service, string sqlPath,
        IReadOnlyList<string> expectedDatabases, Action<string> log, CancellationToken ct) {
        // Sanitized, so a service name can never steer the path the file lands on.
        var remotePath = $"/tmp/{BackupNaming.Sanitize(service)}.sql";
        try {
            await _docker.PutContainerArchiveAsync(containerId, "/tmp", async (stream, token) => {
                await using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true);
                await using var content = File.OpenRead(sqlPath);
                await writer.WriteEntryAsync(
                    new PaxTarEntry(TarEntryType.RegularFile, remotePath[(remotePath.LastIndexOf('/') + 1)..]) {
                        // 0600: the dump carries every role's password hash.
                        Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                        DataStream = content,
                    }, token);
            }, ct);

            return await ReplayRemoteAsync(containerId, connection, service, remotePath, expectedDatabases, log, ct);
        } finally {
            await RemoveRemoteFileAsync(containerId, connection, service, remotePath, log);
        }
    }

    /// <summary>
    /// Replays a dump that is <em>already inside</em> the container — the second half of
    /// <see cref="ReplayAsync"/>, and the whole of what the instance-restore coordinator does (ADR-0027
    /// §5): it is a bare process with the Docker socket and no filesystem in common with the Watchtower
    /// that staged the SQL, so it can only ever replay a path, never push one.
    /// </summary>
    /// <param name="containerId">The database container, already running and ready.</param>
    /// <param name="connection">What <see cref="PreflightAsync"/> established.</param>
    /// <param name="service">The compose service, for the run output.</param>
    /// <param name="remoteSqlPath">Path of the SQL <em>inside</em> the container.</param>
    /// <param name="expectedDatabases">The databases the dump promises; empty skips that check.</param>
    /// <param name="log">Receives operator-facing lines, <c>WARNING: </c> prefix included.</param>
    /// <param name="ct">The run's token.</param>
    /// <exception cref="InvalidOperationException">psql failed, or a database did not come back.</exception>
    public async Task<PostgresReplayResult> ReplayRemoteAsync(
        string containerId, PostgresConnection connection, string service, string remoteSqlPath,
        IReadOnlyList<string> expectedDatabases, Action<string> log, CancellationToken ct) {
        // Terminated immediately before the script runs rather than before the file is staged, so the
        // gap in which something could reconnect and block a DROP DATABASE is as short as it can be.
        await TerminateSessionsAsync(containerId, connection, service, log, ct);

        var replay = await _docker.ExecAsync(
            containerId,
            ["psql", "-U", connection.User, "-d", "postgres", "-w", "-v", "ON_ERROR_STOP=0", "-f", remoteSqlPath],
            stdout: null, ExecEnv(connection.Password), connection.ExecUser, ct);

        // Asked even after a non-zero exit: which databases actually exist is the verdict, and it
        // is also the most useful thing to put in the failure message.
        var (listing, present) = await ListDatabasesAsync(containerId, connection, ct);
        var outcome = PostgresReplayOutcome.Classify(
            replay.ExitCode, replay.Stderr, expectedDatabases, listing.Success ? present : []);
        if (outcome.Failure is { } failure)
            throw new InvalidOperationException($"Replaying the '{service}' dump failed: {failure}");
        _logger.LogInformation(
            "Replayed a dump into container {ContainerId}: {DatabaseCount} database(s) present, "
            + "{DiagnosticCount} psql diagnostic(s)",
            containerId, present.Count, outcome.ErrorLineCount);
        return outcome;
    }

    /// <summary>
    /// Dumps straight to a file <em>inside</em> the container, for the safety copy the instance-restore
    /// coordinator takes before it replaces anything (ADR-0027 §5). The coordinator has nowhere else to
    /// put it: it shares no filesystem with the database container or with Watchtower.
    /// </summary>
    /// <remarks>
    /// The role and password travel as <c>PGUSER</c>/<c>PGPASSWORD</c> rather than as arguments, so
    /// nothing derived from configuration is interpolated into the shell command — the only strings in
    /// it are compile-time constants.
    /// </remarks>
    /// <param name="containerId">The database container.</param>
    /// <param name="connection">What <see cref="PreflightAsync"/> established.</param>
    /// <param name="remotePath">Where to write, inside the container. Its directory is created.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The dump could not be taken.</exception>
    public async Task DumpToContainerFileAsync(
        string containerId, PostgresConnection connection, string remotePath, CancellationToken ct) {
        var directory = remotePath[..remotePath.LastIndexOf('/')];
        string[] env = [
            $"PGUSER={connection.User}",
            .. connection.Password is { Length: > 0 } password ? (string[])[$"PGPASSWORD={password}"] : [],
            $"WT_DUMP_DIR={directory}",
            $"WT_DUMP_FILE={remotePath}",
        ];
        var result = await _docker.ExecAsync(
            containerId,
            ["sh", "-c",
                "mkdir -p \"$WT_DUMP_DIR\" && umask 077 && "
                + "pg_dumpall --clean --if-exists --no-password > \"$WT_DUMP_FILE\""],
            stdout: null, env, connection.ExecUser, ct);
        if (!result.Success)
            throw new InvalidOperationException(
                $"pg_dumpall to {remotePath} failed with exit code {result.ExitCode}: {Tail(result.Stderr)}");
    }

    /// <summary>
    /// Disconnects everything else from the server so <c>--clean</c> can drop the databases. Never
    /// fatal on its own: the replay itself reports what actually went wrong.
    /// </summary>
    private async Task TerminateSessionsAsync(
        string containerId, PostgresConnection connection, string service, Action<string> log, CancellationToken ct) {
        using var stdout = new MemoryStream();
        var result = await _docker.ExecAsync(
            containerId, Psql(connection.User, TerminateSessionsQuery), stdout,
            ExecEnv(connection.Password), connection.ExecUser, ct);
        if (!result.Success) {
            log($"WARNING: could not close the open sessions on '{service}' before the replay "
                + $"(exit code {result.ExitCode}): {Tail(result.Stderr)}");
            return;
        }
        var text = Encoding.UTF8.GetString(stdout.ToArray()).Trim();
        if (int.TryParse(text, out var count) && count > 0)
            log($"WARNING: closed {count} open session(s) on '{service}' before the replay — a database "
                + "cannot be dropped and recreated while something is connected to it.");
    }

    /// <summary>Deletes the SQL from the container's /tmp; best effort, since it is a temp file.</summary>
    private async Task RemoveRemoteFileAsync(
        string containerId, PostgresConnection connection, string service, string remotePath, Action<string> log) {
        try {
            var removal = await _docker.ExecAsync(
                containerId, ["rm", "-f", remotePath], stdout: null, user: connection.ExecUser,
                ct: CancellationToken.None);
            if (!removal.Success)
                log($"WARNING: could not delete {remotePath} inside '{service}' "
                    + $"(exit code {removal.ExitCode}) — it holds the replayed dump, including role "
                    + "password hashes; remove it by hand.");
        } catch (Exception ex) {
            log($"WARNING: could not delete {remotePath} inside '{service}' ({ex.Message}) — it holds the "
                + "replayed dump, including role password hashes; remove it by hand.");
            _logger.LogWarning(ex, "Failed to remove {RemotePath} from container {ContainerId}", remotePath, containerId);
        }
    }

    /// <summary>
    /// Lists the databases that currently exist. Returns the exec result too, so a caller can tell
    /// "the server says there are none" from "the server did not answer".
    /// </summary>
    private async Task<(DockerExecResult Result, IReadOnlyList<string> Databases)> ListDatabasesAsync(
        string containerId, PostgresConnection connection, CancellationToken ct) {
        using var stdout = new MemoryStream();
        var result = await _docker.ExecAsync(
            containerId, Psql(connection.User, DatabaseQuery), stdout,
            ExecEnv(connection.Password), connection.ExecUser, ct);
        return (result, result.Success ? ParseDatabases(stdout.ToArray()) : []);
    }

    /// <summary>
    /// Ends every session but this one, and counts them. <c>datname is not null</c> skips the
    /// background workers, which have no database and cannot be terminated anyway.
    /// </summary>
    private const string TerminateSessionsQuery =
        "select count(pg_terminate_backend(pid)) from pg_stat_activity "
        + "where datname is not null and pid <> pg_backend_pid()";

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
    internal static string Tail(string? stderr) {
        var text = stderr?.Trim() ?? "";
        if (text.Length == 0) return "the process wrote nothing to stderr.";
        return text.Length <= MessageTailChars ? text : $"…{text[^MessageTailChars..]}";
    }
}
