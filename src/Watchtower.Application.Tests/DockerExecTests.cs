using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="DockerEngineClient.ExecAsync"/> — the three calls it is made of (create on the
/// default client, start on the untimed one, inspect back on the default), what it puts in the
/// create body, and how it reads the output back. The routing is the part that has no symptom until
/// it matters: a database dump takes longer than the default client's 100-second ceiling, and a
/// body cut off at that mark looks exactly like a dump that ended.
/// Also covers the push-stream <c>PUT /containers/{id}/archive</c> overload.
/// </summary>
public sealed class DockerExecTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>What the daemon labels a framed exec body with (API 1.42+).</summary>
    private const string Multiplexed = "application/vnd.docker.multiplexed-stream";
    /// <summary>What it labels an unframed one with — a TTY exec, or an older daemon.</summary>
    private const string RawStream = "application/vnd.docker.raw-stream";

    private const byte Stdout = DockerStreamFrame.Stdout;
    private const byte Stderr = DockerStreamFrame.Stderr;

    private static DockerClientEstate Estate() =>
        DockerClientEstate.Create(pruneTimeout: TimeSpan.FromMinutes(30));

    private static HttpContent StreamBody(Stream stream, string contentType) {
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }

    private static HttpResponseMessage JsonBody(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    /// <summary>Makes the untimed client's daemon answer the exec start with <paramref name="body"/>.</summary>
    private static void AnswerStartWith(
        DockerClientEstate estate, Stream body, string contentType = Multiplexed) =>
        estate.LongRunning.Responder = request =>
            request.RequestUri!.AbsolutePath.EndsWith("/start")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = StreamBody(body, contentType) }
                : null;

    // ── Routing and request shape ─────────────────────────────────────────────

    [Fact]
    public async Task TheCreateAndInspectUseTheDefaultClient_TheStartTheUntimedOne() {
        using var estate = Estate();
        var body = new MemoryStream(DockerFrameBuilder.Frame(Stdout, "hi"));
        AnswerStartWith(estate, body);
        using var stdout = new MemoryStream();

        var result = await estate.Client.ExecAsync("abc123", ["echo", "hi"], stdout, ct: Ct);

        Assert.Collection(estate.Default.Requests,
            request => Assert.EndsWith("/containers/abc123/exec", request),
            request => Assert.EndsWith($"/exec/{RecordingHandler.CreatedExecId}/json", request));
        var start = Assert.Single(estate.LongRunning.Requests);
        Assert.EndsWith($"/exec/{RecordingHandler.CreatedExecId}/start", start);
        Assert.True(result.Success);
        Assert.Equal("hi", Encoding.UTF8.GetString(stdout.ToArray()));
        Assert.Equal(2, result.StdoutBytes);
    }

    [Fact]
    public async Task TheCreateBodyAttachesBothOutputsWithoutATty_AndCarriesArgvEnvAndUser() {
        using var estate = Estate();
        AnswerStartWith(estate, new MemoryStream([]));

        // No stdout stream: the caller only wants the exit code, and the output is drained.
        await estate.Client.ExecAsync(
            "db", ["pg_dumpall", "--username=postgres"],
            env: ["PGPASSWORD=hunter2"], user: "postgres", ct: Ct);

        using var created = JsonDocument.Parse(estate.Default.Bodies[0]!);
        var root = created.RootElement;
        // Stdin stays detached on purpose: an attached one turns the start into a hijacked
        // bidirectional connection instead of a response body this client can read.
        Assert.False(root.GetProperty("AttachStdin").GetBoolean());
        Assert.True(root.GetProperty("AttachStdout").GetBoolean());
        Assert.True(root.GetProperty("AttachStderr").GetBoolean());
        // A TTY would merge stderr into stdout and drop the framing with it.
        Assert.False(root.GetProperty("Tty").GetBoolean());
        Assert.Equal(
            new string?[] { "pg_dumpall", "--username=postgres" },
            root.GetProperty("Cmd").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(
            new string?[] { "PGPASSWORD=hunter2" },
            root.GetProperty("Env").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("postgres", root.GetProperty("User").GetString());

        using var started = JsonDocument.Parse(estate.LongRunning.Bodies[0]!);
        Assert.False(started.RootElement.GetProperty("Detach").GetBoolean());
        Assert.False(started.RootElement.GetProperty("Tty").GetBoolean());
    }

    [Fact]
    public async Task WithoutEnvOrUserBothAreLeftUnsetRatherThanSentEmpty() {
        using var estate = Estate();
        AnswerStartWith(estate, new MemoryStream([]));

        await estate.Client.ExecAsync("db", ["true"], ct: Ct);

        using var created = JsonDocument.Parse(estate.Default.Bodies[0]!);
        Assert.Equal(JsonValueKind.Null, created.RootElement.GetProperty("Env").ValueKind);
        Assert.Equal(JsonValueKind.Null, created.RootElement.GetProperty("User").ValueKind);
    }

    // ── Reading the output ────────────────────────────────────────────────────

    [Fact]
    public async Task SplitsTheFramedBodyIntoStdoutAndStderr_AcrossAnEmptyFlushAndASplitHeader() {
        using var estate = Estate();
        var whole = DockerFrameBuilder.Concat(
            DockerFrameBuilder.Frame(Stdout, "row one\n"),
            DockerFrameBuilder.Frame(Stderr, "NOTICE: something\n"),
            DockerFrameBuilder.EmptyFrame(Stdout),
            DockerFrameBuilder.Frame(Stdout, "row two\n"));
        // Chunked the way a socket delivers: the first cut lands inside a header, the second inside
        // a payload.
        AnswerStartWith(estate, new ChunkedStream(whole[..4], whole[4..20], whole[20..]));
        using var stdout = new MemoryStream();

        var result = await estate.Client.ExecAsync("db", ["psql"], stdout, ct: Ct);

        Assert.Equal("row one\nrow two\n", Encoding.UTF8.GetString(stdout.ToArray()));
        Assert.Equal("NOTICE: something\n", result.Stderr);
        Assert.Equal(16, result.StdoutBytes);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task StderrIsKeptAsATailSoAChattyProcessCannotFillMemory() {
        using var estate = Estate();
        var whole = DockerFrameBuilder.Concat(
            DockerFrameBuilder.Frame(Stderr, new string('a', 10_000)),
            DockerFrameBuilder.Frame(Stderr, "the last line, which is the one that says why"));
        AnswerStartWith(estate, new MemoryStream(whole));

        var result = await estate.Client.ExecAsync("db", ["psql"], ct: Ct);

        // The end is what diagnoses a failure; the front is progress noise. The ellipsis says so.
        Assert.StartsWith("…", result.Stderr);
        Assert.EndsWith("the last line, which is the one that says why", result.Stderr);
        Assert.Equal(DockerEngineClient.StderrTailBytes + 1, result.Stderr.Length);
    }

    [Fact]
    public async Task ShortStderrIsKeptWholeWithoutAnEllipsis() {
        using var estate = Estate();
        AnswerStartWith(estate, new MemoryStream(DockerFrameBuilder.Frame(Stderr, "could not connect")));

        var result = await estate.Client.ExecAsync("db", ["psql"], ct: Ct);

        Assert.Equal("could not connect", result.Stderr);
        Assert.Equal(0, result.StdoutBytes);
    }

    [Fact]
    public async Task AnUnframedBodyIsCopiedVerbatim() {
        using var estate = Estate();
        // Bytes that would be read as a frame header if the content type were ignored — an exec
        // whose output happens to start with 0x01 must not lose its first eight bytes.
        var raw = DockerFrameBuilder.Frame(Stdout, "not a frame here");
        AnswerStartWith(estate, new MemoryStream(raw), RawStream);
        using var stdout = new MemoryStream();

        var result = await estate.Client.ExecAsync("db", ["psql"], stdout, ct: Ct);

        Assert.Equal(raw, stdout.ToArray());
        Assert.Equal(raw.Length, result.StdoutBytes);
        // An unframed exec has no separate stderr — the daemon mixed it into the one stream.
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task WithoutAStdoutStreamTheOutputIsDrainedAndStillCounted() {
        using var estate = Estate();
        var whole = DockerFrameBuilder.Concat(
            DockerFrameBuilder.Frame(Stdout, "twelve bytes"),
            DockerFrameBuilder.Frame(Stderr, "and a warning"));
        var body = new ChunkedStream(whole[..10], whole[10..]);
        AnswerStartWith(estate, body);

        var result = await estate.Client.ExecAsync("db", ["psql"], stdout: null, ct: Ct);

        // Draining matters: a body left half-read wedges the connection this client pools.
        Assert.True(body.DrainedToEnd);
        Assert.Equal(12, result.StdoutBytes);
        Assert.Equal("and a warning", result.Stderr);
    }

    // ── The exit code ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ANonZeroExitIsReturnedRatherThanThrown() {
        using var estate = Estate();
        AnswerStartWith(estate, new MemoryStream(DockerFrameBuilder.Frame(Stderr, "role does not exist")));
        estate.Default.Responder = request => request.RequestUri!.AbsolutePath.EndsWith("/json")
            ? JsonBody("""{"Running":false,"ExitCode":3}""")
            : null;

        var result = await estate.Client.ExecAsync("db", ["psql"], ct: Ct);

        // What a failed exec means is the caller's decision — pg_isready's 1, 2 and 3 mean three
        // different things — so the client reports the code and keeps its opinions to itself.
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Success);
        Assert.Equal("role does not exist", result.Stderr);
    }

    [Fact]
    public async Task OutputThatEndedWhileTheExecIsStillRunningIsAFailure() {
        using var estate = Estate();
        AnswerStartWith(estate, new MemoryStream(DockerFrameBuilder.Frame(Stdout, "half a dump")));
        estate.Default.Responder = request => request.RequestUri!.AbsolutePath.EndsWith("/json")
            ? JsonBody("""{"Running":true,"ExitCode":null}""")
            : null;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => estate.Client.ExecAsync("db", ["pg_dumpall"], ct: Ct));

        // The bytes already written look like a complete result; only this says otherwise.
        Assert.Contains("still running when its output ended", ex.Message);
    }

    [Fact]
    public async Task AMissingExitCodeIsAFailureEvenWhenTheDaemonSaysNothingIsRunning() {
        using var estate = Estate();
        AnswerStartWith(estate, new MemoryStream([]));
        estate.Default.Responder = request => request.RequestUri!.AbsolutePath.EndsWith("/json")
            ? JsonBody("""{"Running":false}""")
            : null;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => estate.Client.ExecAsync("db", ["pg_dumpall"], ct: Ct));
    }

    [Fact]
    public async Task ARefusedExecCreateSurfacesTheDaemonsMessage() {
        using var estate = Estate();
        estate.Default.Responder = _ => new HttpResponseMessage(HttpStatusCode.Conflict) {
            Content = new StringContent("""{"message":"container abc is not running"}"""),
        };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => estate.Client.ExecAsync("abc", ["psql"], ct: Ct));

        Assert.Contains("is not running", ex.Message);
        Assert.Empty(estate.LongRunning.Requests);
    }

    // ── The log stream still goes through the same demux ──────────────────────

    [Fact]
    public async Task TheLogStreamStillTurnsFramesIntoTrimmedLines() {
        using var estate = Estate();
        var whole = DockerFrameBuilder.Concat(
            DockerFrameBuilder.Frame(Stdout, "first\n"),
            DockerFrameBuilder.EmptyFrame(Stdout),
            DockerFrameBuilder.Frame(Stderr, "second\r\n"));
        estate.Default.Responder = request => request.RequestUri!.AbsolutePath.EndsWith("/logs")
            ? new HttpResponseMessage(HttpStatusCode.OK) {
                Content = StreamBody(new ChunkedStream(whole[..5], whole[5..]), Multiplexed),
            }
            : null;

        var lines = new List<string>();
        await foreach (var line in estate.Client.StreamLogsAsync("abc", ct: Ct)) lines.Add(line);

        // Unchanged by the lift-out: both streams interleave into one view, newlines stripped.
        Assert.Equal<string>(["first", "second"], lines);
    }

    // ── The push-stream archive PUT ───────────────────────────────────────────

    [Fact]
    public async Task ThePushStreamPutSendsWhatTheCallbackWrote_AsTar() {
        using var estate = Estate();
        string? contentType = null;
        estate.LongRunning.Responder = request => {
            contentType = request.Content?.Headers.ContentType?.MediaType;
            return null;
        };

        await estate.Client.PutContainerArchiveAsync("helper", "/", async (stream, token) => {
            await stream.WriteAsync(Encoding.UTF8.GetBytes("tar bytes written straight "), token);
            await stream.WriteAsync(Encoding.UTF8.GetBytes("into the request"), token);
        }, Ct);

        var request = Assert.Single(estate.LongRunning.Requests);
        Assert.Contains("/containers/helper/archive?path=%2F", request);
        Assert.Equal("application/x-tar", contentType);
        // Nothing was staged in between: the callback's bytes are the request body.
        Assert.Equal("tar bytes written straight into the request", estate.LongRunning.Bodies[0]);
    }

    [Fact]
    public async Task TheStreamOverloadStillSendsTheStreamsBytes() {
        using var estate = Estate();
        using var tar = new MemoryStream(Encoding.UTF8.GetBytes("a manifest, tarred"));

        await estate.Client.PutContainerArchiveAsync("helper", "/backup", tar, Ct);

        Assert.Contains("path=%2Fbackup", Assert.Single(estate.LongRunning.Requests));
        Assert.Equal("a manifest, tarred", estate.LongRunning.Bodies[0]);
    }
}
