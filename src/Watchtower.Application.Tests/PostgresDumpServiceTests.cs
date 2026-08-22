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
