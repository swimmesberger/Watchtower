using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The Postgres dump path (ADR-0017) against a faked daemon: what it sends into the container, what
/// it does with the two output streams, and how it fails. The parts worth pinning are the ones with
/// no symptom until a restore: only stdout may reach the spool file (a NOTICE mixed into the SQL
/// would corrupt the dump), a non-zero exit has to fail the run rather than leave a short file
/// behind, and the password may travel only as an exec environment variable.
/// </summary>
public sealed class PostgresDumpServiceTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Multiplexed = "application/vnd.docker.multiplexed-stream";
    private const byte Stdout = DockerStreamFrame.Stdout;
    private const byte Stderr = DockerStreamFrame.Stderr;

    private static DockerClientEstate Estate() =>
        DockerClientEstate.Create(pruneTimeout: TimeSpan.FromMinutes(30));

    private static PostgresDumpService Service(DockerClientEstate estate) =>
        new(estate.Client, NullLogger<PostgresDumpService>.Instance);

    private static readonly DumpTarget Target = new(
        "db-id", "web-app-db-1", "db", "postgres:16-alpine", DumpEngine.Postgres,
        "web-app_pgdata", ["web-app_pgdata"]);

    /// <summary>Answers every exec start with <paramref name="frames"/>.</summary>
    private static void AnswerStartsWith(DockerClientEstate estate, params byte[][] frames) =>
        estate.LongRunning.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = Body(DockerFrameBuilder.Concat(frames)),
        };

    private static HttpContent Body(byte[] bytes) {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(Multiplexed);
        return content;
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Makes the exec inspects answer with <paramref name="exitCodes"/>, in order.</summary>
    private static void AnswerExitCodes(DockerClientEstate estate, params int[] exitCodes) {
        var next = 0;
        estate.Default.Responder = request => {
            var path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/exec/", StringComparison.Ordinal)
                || !path.EndsWith("/json", StringComparison.Ordinal)) return null;
            var code = exitCodes[Math.Min(next++, exitCodes.Length - 1)];
            return Json($$"""{"Running":false,"ExitCode":{{code}}}""");
        };
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"watchtower-dump-test-{Guid.NewGuid():N}.sql");

    // ── The dump ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnlyStdoutReachesTheSpoolFile_AndStderrBecomesAWarning() {
        using var estate = Estate();
        AnswerStartsWith(estate,
            DockerFrameBuilder.Frame(Stdout, "DROP DATABASE app;\n"),
            DockerFrameBuilder.Frame(Stderr, "NOTICE: something harmless\n"),
            DockerFrameBuilder.Frame(Stdout, "CREATE DATABASE app;\n"));
        var connection = new PostgresConnection("app", "hunter2", null) { Databases = ["app", "postgres"] };
        var log = new List<string>();
        var spool = TempPath();

        try {
            var result = await Service(estate).DumpAsync(Target, connection, spool, log.Add, Ct);

            // A NOTICE inside the SQL would break the replay — the framing is what keeps it out.
            Assert.Equal("DROP DATABASE app;\nCREATE DATABASE app;\n", await File.ReadAllTextAsync(spool, Ct));
            Assert.Equal(new FileInfo(spool).Length, result.SizeBytes);
            Assert.Equal(["app", "postgres"], result.Databases);
            Assert.Contains(log, l => l.StartsWith("WARNING: pg_dumpall for 'db' wrote 1 line(s) to stderr", StringComparison.Ordinal));
            Assert.Contains(log, l => l == $"Dump of 'db' complete: {result.SizeBytes} bytes, 2 database(s).");
        } finally {
            File.Delete(spool);
        }
    }

    [Fact]
    public async Task TheDumpRunsPgDumpallWithTheRoleAndThePasswordOnlyInTheEnvironment() {
        using var estate = Estate();
        AnswerStartsWith(estate, DockerFrameBuilder.Frame(Stdout, "-- dump\n"));
        var connection = new PostgresConnection("app", "hunter2", "postgres");
        var spool = TempPath();

        try {
            await Service(estate).DumpAsync(Target, connection, spool, _ => { }, Ct);
        } finally {
            File.Delete(spool);
        }

        using var created = JsonDocument.Parse(estate.Default.Bodies[0]!);
        var root = created.RootElement;
        Assert.Equal(
            new string?[] { "pg_dumpall", "--username=app", "--clean", "--if-exists", "--no-password" },
            root.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray());
        // The password is a value the daemon gets, never an argument other processes could read off
        // the command line and never anything the run output sees.
        Assert.Equal(
            new string?[] { "PGPASSWORD=hunter2" },
            root.GetProperty("Env").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("postgres", root.GetProperty("User").GetString());
    }

    [Fact]
    public async Task ANonZeroExitFailsTheRunAndCarriesTheEndOfStderr() {
        using var estate = Estate();
        AnswerStartsWith(estate,
            DockerFrameBuilder.Frame(Stdout, "-- half a dump\n"),
            DockerFrameBuilder.Frame(Stderr, "pg_dumpall: error: query failed: server closed the connection"));
        AnswerExitCodes(estate, 1);
        var spool = TempPath();

        try {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                Service(estate).DumpAsync(Target, new PostgresConnection("app", null, null), spool, _ => { }, Ct));

            // Falling back to a hot file snapshot here would produce an archive that looks fine and
            // restores torn — yesterday's archive is the better thing to keep.
            Assert.Contains("pg_dumpall for service 'db' failed with exit code 1", ex.Message);
            Assert.Contains("server closed the connection", ex.Message);
        } finally {
            File.Delete(spool);
        }
    }

    [Fact]
    public async Task WithoutAPasswordNoEnvironmentIsSentAtAll() {
        using var estate = Estate();
        AnswerStartsWith(estate, DockerFrameBuilder.Frame(Stdout, "-- dump\n"));
        var spool = TempPath();

        try {
            await Service(estate).DumpAsync(
                Target, new PostgresConnection("postgres", null, null), spool, _ => { }, Ct);
        } finally {
            File.Delete(spool);
        }

        using var created = JsonDocument.Parse(estate.Default.Bodies[0]!);
        Assert.Equal(JsonValueKind.Null, created.RootElement.GetProperty("Env").ValueKind);
    }

    // ── The preflight ────────────────────────────────────────────────────────

    /// <summary>Answers the container inspect with an environment carrying the credentials.</summary>
    private static void AnswerInspectWith(DockerClientEstate estate, string[] env, params int[] exitCodes) {
        var next = 0;
        estate.Default.Responder = request => {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/exec/", StringComparison.Ordinal)
                && path.EndsWith("/json", StringComparison.Ordinal)) {
                var code = exitCodes[Math.Min(next++, exitCodes.Length - 1)];
                return Json($$"""{"Running":false,"ExitCode":{{code}}}""");
            }
            if (!path.EndsWith("/json", StringComparison.Ordinal)) return null;
            var quoted = string.Join(",", env.Select(e => $"\"{e}\""));
            return Json(
                """{"Id":"db-id","Image":"sha256:test","Config":{"Image":"postgres:16-alpine","Env":["""
                + quoted + "]}}");
        };
    }

    [Fact]
    public async Task ThePreflightReadsTheCredentialsOffTheContainerAndListsTheDatabases() {
        using var estate = Estate();
        AnswerInspectWith(estate, ["POSTGRES_USER=app", "POSTGRES_PASSWORD=hunter2", "PGDATA=/var/lib/postgresql/data"], 0);
        AnswerStartsWith(estate, DockerFrameBuilder.Frame(Stdout, "postgres\napp\n"));
        var log = new List<string>();

        var connection = await Service(estate).PreflightAsync(Target, log.Add, Ct);

        Assert.Equal("app", connection.User);
        Assert.Equal("hunter2", connection.Password);
        Assert.Null(connection.ExecUser);
        Assert.Equal(["app", "postgres"], connection.Databases);
        Assert.Contains(log, l => l.Contains("answers as role 'app'") && l.Contains("2 database(s)"));

        // Probe, then the database listing — both non-interactive, both with the password in the
        // environment rather than on the command line.
        using var probe = JsonDocument.Parse(estate.Default.Bodies[1]!);
        Assert.Equal(
            new string?[] { "psql", "-U", "app", "-d", "postgres", "-w", "-tAc", "select 1" },
            probe.RootElement.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(
            new string?[] { "PGPASSWORD=hunter2" },
            probe.RootElement.GetProperty("Env").EnumerateArray().Select(e => e.GetString()).ToArray());
        using var listing = JsonDocument.Parse(estate.Default.Bodies[3]!);
        Assert.Contains(
            "pg_database",
            listing.RootElement.GetProperty("Cmd").EnumerateArray().Last().GetString() ?? "");
    }

    [Fact]
    public async Task WithoutPostgresUserItConnectsAsPostgres() {
        using var estate = Estate();
        AnswerInspectWith(estate, [], 0);
        AnswerStartsWith(estate, DockerFrameBuilder.Frame(Stdout, "postgres\n"));

        var connection = await Service(estate).PreflightAsync(Target, _ => { }, Ct);

        Assert.Equal("postgres", connection.User);
        Assert.Null(connection.Password);
    }

    [Fact]
    public async Task AFirstProbeThatCannotAuthenticateIsRetriedAsThePostgresUser() {
        using var estate = Estate();
        // Exit 2 on the image's default exec user, then 0 — the peer-authentication case, where the
        // connection is only accepted when the OS user is postgres too.
        AnswerInspectWith(estate, ["POSTGRES_USER=app"], 2, 0, 0);
        AnswerStartsWith(estate, DockerFrameBuilder.Frame(Stdout, "app\n"));

        var connection = await Service(estate).PreflightAsync(Target, _ => { }, Ct);

        Assert.Equal("postgres", connection.ExecUser);
        using var first = JsonDocument.Parse(estate.Default.Bodies[1]!);
        Assert.Equal(JsonValueKind.Null, first.RootElement.GetProperty("User").ValueKind);
        using var retry = JsonDocument.Parse(estate.Default.Bodies[3]!);
        Assert.Equal("postgres", retry.RootElement.GetProperty("User").GetString());
        // The listing runs as the user that worked, not as the one that did not.
        using var listing = JsonDocument.Parse(estate.Default.Bodies[5]!);
        Assert.Equal("postgres", listing.RootElement.GetProperty("User").GetString());
    }

    [Fact]
    public async Task BothProbesFailingStopsTheRunBeforeAnythingIsStopped() {
        using var estate = Estate();
        AnswerInspectWith(estate, ["POSTGRES_USER=app"], 2);
        AnswerStartsWith(estate,
            DockerFrameBuilder.Frame(Stderr, "psql: error: FATAL: password authentication failed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Service(estate).PreflightAsync(Target, _ => { }, Ct));

        Assert.Contains("Could not connect to the database of service 'db' as role 'app'", ex.Message);
        Assert.Contains("password authentication failed", ex.Message);
    }

    // ── Waiting for a database that was just started ─────────────────────────

    /// <summary>A service whose readiness ceiling is short enough for a test to reach.</summary>
    private static PostgresDumpService ImpatientService(DockerClientEstate estate) =>
        new(estate.Client, NullLogger<PostgresDumpService>.Instance,
            readyTimeout: TimeSpan.FromMilliseconds(150), readyPollInterval: TimeSpan.FromMilliseconds(10));

    /// <summary>How many execs were created — one per probe.</summary>
    private static int ExecCount(DockerClientEstate estate) =>
        estate.Default.Requests.Count(r => r.EndsWith("/exec", StringComparison.Ordinal));

    private static readonly PostgresConnection Postgres = new("postgres", null, null);

    [Fact]
    public async Task TheReadinessWaitKeepsPollingUntilTheServerAnswers() {
        using var estate = Estate();
        // 2 = no response yet, 1 = up but still rejecting connections, 0 = ready.
        AnswerExitCodes(estate, 2, 1, 0);
        var log = new List<string>();

        await ImpatientService(estate).WaitReadyAsync("db-id", Postgres, "db", log.Add, Ct);

        Assert.Equal(3, ExecCount(estate));
        Assert.Contains(log, l => l.StartsWith("Waiting for postgres in 'db' to accept connections…", StringComparison.Ordinal)
            && l.Contains("ready after"));
        using var probe = JsonDocument.Parse(estate.Default.Bodies[0]!);
        Assert.Equal(
            new string?[] { "pg_isready", "-U", "postgres" },
            probe.RootElement.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task AServerThatNeverComesUpFailsAtTheCeiling() {
        using var estate = Estate();
        AnswerExitCodes(estate, 2);

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => ImpatientService(estate).WaitReadyAsync("db-id", Postgres, "db", _ => { }, Ct));

        // Not an OperationCanceledException: the caller treats that as "we are shutting down".
        Assert.Contains("did not accept connections within", ex.Message);
        Assert.Contains("The dump was not replayed.", ex.Message);
    }

    [Fact]
    public async Task ApgIsreadyThatCannotRunAtAllFailsOnTheFirstProbe() {
        using var estate = Estate();
        AnswerExitCodes(estate, 3);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ImpatientService(estate).WaitReadyAsync("db-id", Postgres, "db", _ => { }, Ct));

        // Exit 3 is "bad invocation", which polling can never turn into a success.
        Assert.Contains("pg_isready refused the call", ex.Message);
        Assert.Equal(1, ExecCount(estate));
    }

    // ── The replay ───────────────────────────────────────────────────────────

    /// <summary>Answers each exec start with the next body, so one test can script several execs.</summary>
    private static void AnswerStartsInOrder(DockerClientEstate estate, params byte[][] bodies) {
        var next = 0;
        estate.LongRunning.Responder = _ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = Body(bodies[Math.Min(next++, bodies.Length - 1)]),
        };
    }

    private static byte[] Out(string text) => DockerFrameBuilder.Frame(Stdout, text);
    private static byte[] Err(string text) => DockerFrameBuilder.Frame(Stderr, text);

    private static string WriteSql(string sql) {
        var path = Path.Combine(Path.GetTempPath(), $"watchtower-replay-test-{Guid.NewGuid():N}.sql");
        File.WriteAllText(path, sql);
        return path;
    }

    /// <summary>
    /// Where the exec carrying <paramref name="fragment"/> in its command sits among the recorded
    /// requests. Found rather than counted: one exec is two HTTP calls and a file push is a third
    /// shape, so fixed offsets say more about the transport than about the order under test.
    /// </summary>
    private static int ExecIndex(DockerClientEstate estate, string fragment) {
        var index = estate.Default.Bodies.FindIndex(
            b => b is not null && b.Contains(fragment, StringComparison.Ordinal));
        Assert.True(index >= 0, $"no exec was recorded whose command contains '{fragment}'");
        return index;
    }

    [Fact]
    public async Task TheReplayTerminatesSessions_CopiesTheSql_RunsPsql_AndCleansUp() {
        using var estate = Estate();
        AnswerStartsInOrder(estate,
            Out("2\n"),                                        // sessions terminated
            Err("psql:/tmp/db.sql:12: ERROR:  role \"postgres\" already exists\n"),
            Out("app\npostgres\n"),                            // databases afterwards
            []);                                               // rm
        var sql = WriteSql("DROP DATABASE app;\n");
        var log = new List<string>();

        PostgresReplayResult result;
        try {
            result = await Service(estate).ReplayAsync(
                "db-id", new PostgresConnection("app", "hunter2", "postgres"), "db", sql,
                ["app", "postgres"], log.Add, Ct);
        } finally {
            File.Delete(sql);
        }

        // The SQL goes in as a tar at /tmp, streamed from the host file.
        var put = estate.Default.Requests.FindIndex(r => r.Contains("/archive?path=", StringComparison.Ordinal));
        Assert.Contains("/containers/db-id/archive?path=%2Ftmp", estate.Default.Requests[put]);
        await using var reader = new TarReader(new MemoryStream(estate.Default.BodyBytes[put]!));
        var entry = await reader.GetNextEntryAsync(cancellationToken: Ct);
        Assert.Equal("db.sql", entry!.Name);
        // 0600: the dump carries every role's password hash.
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, entry.Mode);

        // Every session has to go before psql: --clean cannot DROP DATABASE under a live connection, and
        // the script would then merge the dump into the old database instead of replacing it. It happens
        // *after* the file is staged rather than before, so the window in which something can reconnect
        // is as short as it can be — and so the coordinator's replay-a-file-already-there path
        // (ReplayRemoteAsync, ADR-0027) terminates them exactly once.
        var terminateAt = ExecIndex(estate, "pg_terminate_backend");
        var replayAt = ExecIndex(estate, "ON_ERROR_STOP=0");
        Assert.True(put < terminateAt, "the SQL should be staged before the sessions are closed");
        Assert.True(terminateAt < replayAt, "the sessions should be closed before psql runs");
        Assert.Contains(log, l => l.StartsWith("WARNING: closed 2 open session(s) on 'db'", StringComparison.Ordinal));

        using var replay = JsonDocument.Parse(estate.Default.Bodies[replayAt]!);
        Assert.Equal(
            new string?[] { "psql", "-U", "app", "-d", "postgres", "-w", "-v", "ON_ERROR_STOP=0", "-f", "/tmp/db.sql" },
            replay.RootElement.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray());

        // The diagnostics are counted, not acted on — this exact error is in every --clean script.
        Assert.Equal(1, result.ErrorLineCount);
        Assert.Single(result.SampleErrors);
        Assert.Empty(result.MissingDatabases);
        Assert.True(result.Succeeded);

        // And the SQL does not stay behind in the container.
        using var removal = JsonDocument.Parse(estate.Default.Bodies[^2]!);
        Assert.Equal(
            new string?[] { "rm", "-f", "/tmp/db.sql" },
            removal.RootElement.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public async Task APsqlThatFailsOutrightFailsTheReplayWithItsStderr() {
        using var estate = Estate();
        AnswerStartsInOrder(estate,
            Out("0\n"),
            Err("psql: error: could not connect to server: No such file or directory"),
            Out("postgres\n"),
            []);
        // terminate, psql, listing, rm.
        AnswerExitCodes(estate, 0, 2, 0, 0);
        var sql = WriteSql("DROP DATABASE app;\n");

        try {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(estate).ReplayAsync(
                "db-id", Postgres, "db", sql, ["app"], _ => { }, Ct));

            Assert.Contains("Replaying the 'db' dump failed", ex.Message);
            Assert.Contains("psql exited with code 2", ex.Message);
            Assert.Contains("could not connect to server", ex.Message);
        } finally {
            File.Delete(sql);
        }
    }

    [Fact]
    public async Task ADatabaseThatIsNotThereAfterwardsFailsTheReplay() {
        using var estate = Estate();
        AnswerStartsInOrder(estate,
            Out("0\n"),
            [],                       // psql said nothing and exited 0
            Out("postgres\n"),        // …but the application database is not there
            []);
        var sql = WriteSql("-- a dump that restores nothing\n");

        try {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service(estate).ReplayAsync(
                "db-id", Postgres, "db", sql, ["app", "postgres"], _ => { }, Ct));

            // The exit code cannot be the judge here — ON_ERROR_STOP=0 means psql exits 0 having
            // achieved nothing at all. The databases are the evidence.
            Assert.Contains("the database(s) app are not on the server afterwards", ex.Message);
        } finally {
            File.Delete(sql);
        }
    }

    [Fact]
    public async Task AFailedCleanupIsAWarning_NotAFailedRestore() {
        using var estate = Estate();
        AnswerStartsInOrder(estate, Out("0\n"), [], Out("app\n"), Err("rm: permission denied"));
        // terminate, psql, listing, rm.
        AnswerExitCodes(estate, 0, 0, 0, 1);
        var sql = WriteSql("-- dump\n");
        var log = new List<string>();

        try {
            var result = await Service(estate).ReplayAsync(
                "db-id", Postgres, "db", sql, ["app"], log.Add, Ct);
            Assert.True(result.Succeeded);
        } finally {
            File.Delete(sql);
        }

        Assert.Contains(log, l => l.StartsWith("WARNING: could not delete /tmp/db.sql inside 'db'", StringComparison.Ordinal)
            && l.Contains("password hashes"));
    }

    // ── The connection record ────────────────────────────────────────────────

    [Fact]
    public void TheConnectionNeverPrintsThePassword() {
        var text = new PostgresConnection("app", "hunter2", "postgres").ToString();

        // One Log…("{Connection}", connection) is all it would take otherwise.
        Assert.DoesNotContain("hunter2", text);
        Assert.Contains("User = app", text);
        Assert.Contains("ExecUser = postgres", text);
    }
}
