using System.Net;
using System.Text;
using Watchtower.Application.Services;

namespace Watchtower.Application.Tests;

/// <summary>
/// Records the paths it is asked for and answers each with a body the Docker DTOs can parse.
/// With <c>hang: true</c> it stands in for a daemon that accepted the request and then went quiet —
/// the shape the client-side ceilings exist for.
/// </summary>
internal sealed class RecordingHandler(bool hang = false, Action? onCancelled = null) : HttpMessageHandler {
    public List<string> Requests { get; } = [];
    public bool Disposed { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add(request.RequestUri!.PathAndQuery);
        if (hang) {
            try {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            } catch (OperationCanceledException) {
                // Lets a test slip an event in between the cancellation and the caller's catch
                // filter — the window a shutdown racing an expired cap would land in.
                onCancelled?.Invoke();
                throw;
            }
        }
        return new HttpResponseMessage(HttpStatusCode.OK) {
            Content = new StringContent(BodyFor(path), Encoding.UTF8, "application/json"),
        };
    }

    // The wait and inspect responses have `required` members, so an empty object would not
    // deserialize. Inspect answers "running", which is what sends the reconcile into the wait.
    private static string BodyFor(string path) =>
        path.EndsWith("/containers/json") ? "[]"
        : path.EndsWith("/wait") ? """{"StatusCode":0}"""
        : path.EndsWith("/json") ? """{"Id":"c","Image":"sha256:test","State":{"Status":"running","ExitCode":0}}"""
        : "{}";

    protected override void Dispose(bool disposing) {
        Disposed = true;
        base.Dispose(disposing);
    }
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
    public static DockerClientEstate Create(
        TimeSpan pruneTimeout, bool hangLongRunning = false, Action? onLongRunningCancelled = null) {
        var defaultHandler = new RecordingHandler();
        var longRunningHandler = new RecordingHandler(hangLongRunning, onLongRunningCancelled);
        var baseAddress = new Uri("http://docker");
        var defaultClient = new HttpClient(defaultHandler, disposeHandler: false) { BaseAddress = baseAddress };
        var longRunningClient = new HttpClient(longRunningHandler, disposeHandler: false) {
            BaseAddress = baseAddress,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var client = new DockerEngineClient("1.43", defaultClient, longRunningClient, pruneTimeout);
        return new DockerClientEstate(client, defaultHandler, longRunningHandler, defaultClient, longRunningClient);
    }

    public void Dispose() {
        Client.Dispose();
        _defaultClient.Dispose();
        _longRunningClient.Dispose();
        Default.Dispose();
        LongRunning.Dispose();
    }
}
