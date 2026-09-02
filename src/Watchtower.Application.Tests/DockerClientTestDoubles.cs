using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Watchtower.Application.Services;

namespace Watchtower.Application.Tests;

/// <summary>
/// A container as <c>GET /containers/json</c> reports it: enough of the shape for
/// <see cref="DockerContainerInfo"/> to deserialize — its required members included — and nothing else.
/// </summary>
/// <param name="PublicPort">The host port it publishes; null for a port exposed but not published.</param>
/// <param name="Protocol">
/// <c>tcp</c> or <c>udp</c>; an empty string stands in for the daemon leaving the field off, which
/// readers take as tcp.
/// </param>
/// <param name="State">Any state at all — a stopped container still holds its declared bindings.</param>
internal sealed record ListedContainer(
    string Id,
    string Name,
    int? PublicPort,
    int PrivatePort = 8096,
    string Protocol = "tcp",
    string State = "running",
    string? Project = null,
    string? Service = null) {
    public JsonObject ToJson() {
        var labels = new JsonObject();
        if (Project is not null) labels["com.docker.compose.project"] = Project;
        if (Service is not null) labels["com.docker.compose.service"] = Service;
        var port = new JsonObject {
            ["IP"] = "0.0.0.0",
            ["PrivatePort"] = PrivatePort,
            ["Type"] = Protocol,
        };
        if (PublicPort is { } published) port["PublicPort"] = published;
        return new JsonObject {
            ["Id"] = Id,
            ["Names"] = new JsonArray($"/{Name}"),
            ["Image"] = "registry.invalid/app:latest",
            ["State"] = State,
            ["Status"] = State,
            ["Labels"] = labels,
            ["Ports"] = new JsonArray(port),
        };
    }

    public static string Body(params ListedContainer[] containers) =>
        new JsonArray([.. containers.Select(c => (JsonNode)c.ToJson())]).ToJsonString();
}

/// <summary>
/// Records the paths it is asked for and answers each with a body the Docker DTOs can parse.
/// With <c>hang: true</c> it stands in for a daemon that accepted the request and then went quiet —
/// the shape the client-side ceilings exist for. <paramref name="hangWhen"/> narrows that to the
/// requests it matches, which is what a test needs once more than one call shares the untimed
/// client: the self-update pull and the coordinator wait both go through it, and hanging the pull
/// means the apply never reaches the watch the test is actually about.
/// </summary>
internal sealed class RecordingHandler(
    bool hang = false,
    Action? onCancelled = null,
    TimeSpan? delay = null,
    Func<HttpRequestMessage, bool>? hangWhen = null) : HttpMessageHandler {
    public List<string> Requests { get; } = [];
    /// <summary>What each request carried, decoded as UTF-8; parallel to <see cref="Requests"/>.</summary>
    public List<string?> Bodies { get; } = [];
    /// <summary>The same bodies unmangled, for the requests that send tar rather than JSON.</summary>
    public List<byte[]?> BodyBytes { get; } = [];
    /// <summary>The last <c>X-Registry-Auth</c> header seen, still base64-encoded; null when none was sent.</summary>
    public string? RegistryAuth { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>
    /// Consulted before <see cref="BodyFor"/>: lets one test answer a specific request with
    /// something the canned JSON chain cannot express — a multiplexed exec body, a chosen exit
    /// code, an error status. Returning null falls through to the default answer.
    /// </summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Responder { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(request.RequestUri!.PathAndQuery);
        RegistryAuth = request.Headers.TryGetValues("X-Registry-Auth", out var auth)
            ? auth.FirstOrDefault()
            : null;
        // Reading the content here is also what runs a push-stream body's writer callback.
        var bytes = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        BodyBytes.Add(bytes);
        Bodies.Add(bytes is null ? null : Encoding.UTF8.GetString(bytes));
        // A daemon that answers, but not instantly — enough for a ceiling to expire around it.
        if (delay is { } pause) await Task.Delay(pause, cancellationToken);
        if (hang && (hangWhen?.Invoke(request) ?? true)) {
            try {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            } catch (OperationCanceledException) {
                // Lets a test slip an event in between the cancellation and the caller's catch
                // filter — the window a shutdown racing an expired cap would land in.
                onCancelled?.Invoke();
                throw;
            }
        }
        if (Responder?.Invoke(request) is { } answer) return answer;
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(BodyFor(path), Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>The container id every create answers with; long enough for the callers' [..12] logs.</summary>
    public const string CreatedContainerId = "c0ffee1234567890abcdef";

    /// <summary>The exec id every <c>POST /containers/{id}/exec</c> answers with.</summary>
    public const string CreatedExecId = "exec0123456789abcdef";

    // The wait, create, exec and inspect responses have `required` members or a nullable exit code,
    // so an empty object would not deserialize (or would read as "still running"). Container
    // inspect answers "running", which is what sends the reconcile into the wait.
    private static string BodyFor(string path) =>
        path.EndsWith("/containers/json") ? "[]"
        : path.EndsWith("/wait") ? """{"StatusCode":0}"""
        : path.EndsWith("/containers/create") ? $$"""{"Id":"{{CreatedContainerId}}"}"""
        : path.EndsWith("/exec") ? $$"""{"Id":"{{CreatedExecId}}"}"""
        : path.Contains("/exec/") && path.EndsWith("/json") ? """{"Running":false,"ExitCode":0}"""
        : path.EndsWith("/json") ? InspectBody
        : "{}";

    /// <summary>A running container, with the image name self-detection reads off Config.</summary>
    private static readonly string InspectBody = $$$"""
        {"Id":"{{{CreatedContainerId}}}","Image":"sha256:test","Config":{"Image":"registry.invalid/watchtower:latest"},"State":{"Status":"running","ExitCode":0}}
        """;

    protected override void Dispose(bool disposing) {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Builds bodies in Docker's multiplexed stream format: an 8-byte header (stream type, three
/// reserved zeros, big-endian payload length) in front of each payload.
/// </summary>
internal static class DockerFrameBuilder {
    public static byte[] Frame(byte streamType, string payload) =>
        Frame(streamType, Encoding.UTF8.GetBytes(payload));

    public static byte[] Frame(byte streamType, byte[] payload) {
        var frame = new byte[8 + payload.Length];
        frame[0] = streamType;
        frame[4] = (byte)(payload.Length >> 24);
        frame[5] = (byte)(payload.Length >> 16);
        frame[6] = (byte)(payload.Length >> 8);
        frame[7] = (byte)payload.Length;
        payload.CopyTo(frame, 8);
        return frame;
    }

    /// <summary>A header announcing no payload at all — what the daemon emits on an empty flush.</summary>
    public static byte[] EmptyFrame(byte streamType) => Frame(streamType, []);

    public static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}

/// <summary>
/// Hands its content out in exactly the pieces it was given, one per read — the shape a socket
/// delivers in, where a frame header can arrive split down the middle. Reads never span two chunks.
/// </summary>
internal sealed class ChunkedStream(params byte[][] chunks) : Stream {
    private readonly Queue<byte[]> _chunks = new(chunks);
    private byte[] _current = [];
    private int _offset;

    /// <summary>True once a read has run past the last chunk — i.e. the body was fully consumed.</summary>
    public bool DrainedToEnd { get; private set; }

    public override int Read(byte[] buffer, int offset, int count) {
        while (_offset == _current.Length) {
            if (_chunks.Count == 0) {
                DrainedToEnd = true;
                return 0;
            }
            _current = _chunks.Dequeue();
            _offset = 0;
        }
        var take = Math.Min(count, _current.Length - _offset);
        Array.Copy(_current, _offset, buffer, offset, take);
        _offset += take;
        return take;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) {
        var rented = new byte[buffer.Length];
        var read = Read(rented, 0, rented.Length);
        rented.AsMemory(0, read).CopyTo(buffer);
        return ValueTask.FromResult(read);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>
/// A <see cref="DockerEngineClient"/> wired to two separate handlers — one per client — so which of
/// the two a call took is observable, and so the long-running side can be made to hang on its own.
/// </summary>
internal sealed class DockerClientEstate : IDisposable {
    private readonly HttpClient _defaultClient;
    private readonly HttpClient _longRunningClient;

    private DockerClientEstate(
        DockerEngineClient client,
        RecordingHandler defaultHandler,
        RecordingHandler longRunningHandler,
        HttpClient defaultClient,
        HttpClient longRunningClient) {
        Client = client;
        Default = defaultHandler;
        LongRunning = longRunningHandler;
        _defaultClient = defaultClient;
        _longRunningClient = longRunningClient;
    }

    public DockerEngineClient Client { get; }
    public RecordingHandler Default { get; }
    public RecordingHandler LongRunning { get; }

    /// <param name="pruneTimeout">Stands in for the real 30-minute cap.</param>
    /// <param name="hangLongRunning">Makes the untimed client's daemon never answer.</param>
    /// <param name="onLongRunningCancelled">Runs when a hanging long-running call is cancelled.</param>
    /// <param name="defaultDelay">Makes every call on the default client take this long to answer.</param>
    /// <param name="hangLongRunningWhen">
    /// Narrows <paramref name="hangLongRunning"/> to the requests it matches; without it every call
    /// on the untimed client hangs, including ones a test needs to get past to reach the one it is
    /// about.
    /// </param>
    public static DockerClientEstate Create(
        TimeSpan pruneTimeout,
        bool hangLongRunning = false,
        Action? onLongRunningCancelled = null,
        TimeSpan? defaultDelay = null,
        Func<HttpRequestMessage, bool>? hangLongRunningWhen = null) {
        var defaultHandler = new RecordingHandler(delay: defaultDelay);
        var longRunningHandler = new RecordingHandler(
            hangLongRunning, onLongRunningCancelled, hangWhen: hangLongRunningWhen);
        var baseAddress = new Uri("http://docker");
        var defaultClient = new HttpClient(defaultHandler, disposeHandler: false) { BaseAddress = baseAddress };
        var longRunningClient = new HttpClient(longRunningHandler, disposeHandler: false) {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new DockerEngineClient("1.43", defaultClient, longRunningClient, pruneTimeout);
        return new DockerClientEstate(client, defaultHandler, longRunningHandler, defaultClient, longRunningClient);
    }

    /// <summary>
    /// Makes <c>GET /containers/json</c> answer with these containers, leaving whatever else the
    /// double was already answering — a self-inspect, most usefully — where it was.
    /// </summary>
    public void ListsContainers(params ListedContainer[] containers) =>
        AnswerContainerList(_ => new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(
                ListedContainer.Body(containers), Encoding.UTF8, "application/json"),
        });

    /// <summary>A daemon that cannot answer the container list — the fail-open case.</summary>
    public void FailsTheContainerList() =>
        AnswerContainerList(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError) {
            Content = new StringContent("""{"message":"daemon is not running"}""", Encoding.UTF8, "application/json"),
        });

    /// <summary>
    /// A daemon that cannot answer a container <em>inspect</em> — which is how Watchtower identifies its
    /// own container, so it stands in for every reason that identification can fail.
    /// </summary>
    public void FailsSelfInspection() {
        var previous = Default.Responder;
        Default.Responder = request => {
            var path = request.RequestUri!.AbsolutePath;
            return path.EndsWith("/json", StringComparison.Ordinal)
                && !path.EndsWith("/containers/json", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound) {
                    Content = new StringContent("""{"message":"No such container"}""", Encoding.UTF8, "application/json"),
                }
                : previous?.Invoke(request);
        };
    }

    private void AnswerContainerList(Func<HttpRequestMessage, HttpResponseMessage> answer) {
        // Chained rather than assigned, so a double that is already answering a self-inspect keeps
        // doing so; the two paths are distinct (`/containers/{id}/json` is not `/containers/json`).
        var previous = Default.Responder;
        Default.Responder = request =>
            request.RequestUri!.AbsolutePath.EndsWith("/containers/json", StringComparison.Ordinal)
                ? answer(request)
                : previous?.Invoke(request);
    }

    public void Dispose() {
        Client.Dispose();
        _defaultClient.Dispose();
        _longRunningClient.Dispose();
        Default.Dispose();
        LongRunning.Dispose();
    }
}
