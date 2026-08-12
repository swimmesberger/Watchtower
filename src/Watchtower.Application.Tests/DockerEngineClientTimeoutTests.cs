using System.Net;
using System.Text;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers how <see cref="DockerEngineClient"/> splits its calls across two HttpClients: the default
/// one keeps HttpClient's 100-second ceiling so UI-facing calls fail fast on a wedged socket, while
/// the calls whose duration belongs to the host — the container wait and the image prune — go
/// through an untimed one. Getting the routing wrong is invisible until a self-update watch or a
/// prune of a long layer backlog is abandoned at the 100-second mark for no reason at all.
/// </summary>
public sealed class DockerEngineClientTimeoutTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ApiVersion = "1.43";

    // ── Client construction ───────────────────────────────────────────────────

    [Fact]
    public async Task TheTwoClientsAreDistinct_ShareOneHandler_AndCarryDifferentTimeouts() {
        using var handler = new RecordingHandler();
        var (defaultClient, longRunningClient) = DockerEngineClient.CreateClients(handler);
        using (defaultClient)
        using (longRunningClient) {
            Assert.NotSame(defaultClient, longRunningClient);

            // The default client is left untouched at HttpClient's own default — raising it would
            // make every list/inspect call hang on a daemon that has stopped answering.
            Assert.Equal(TimeSpan.FromSeconds(100), defaultClient.Timeout);
            Assert.Equal(Timeout.InfiniteTimeSpan, longRunningClient.Timeout);

            // One handler, therefore one connection pool over the Docker socket: both clients
            // deliver into the very same handler instance.
            await defaultClient.GetAsync("http://docker/_ping", Ct);
            await longRunningClient.GetAsync("http://docker/_ping", Ct);
            Assert.Equal(2, handler.Requests.Count);
        }

        // Neither client owns the handler (disposeHandler: false), so disposing both leaves it
        // untouched — otherwise the first disposal would tear the pool out from under the second.
        Assert.False(handler.Disposed);
    }

    [Fact]
    public async Task Dispose_DisposesBothClients_AndLeavesABorrowedHandlerAlone() {
        var handler = new RecordingHandler();
        var (defaultClient, longRunningClient) = DockerEngineClient.CreateClients(handler);
        var client = new DockerEngineClient(ApiVersion, defaultClient, longRunningClient, TimeSpan.FromMinutes(30));

        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => defaultClient.GetAsync("http://docker/_ping", Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => longRunningClient.GetAsync("http://docker/_ping", Ct));
        // This instance did not build the handler, so it does not dispose it; the production
        // constructor is the one that owns and disposes the handler it created.
        Assert.False(handler.Disposed);
        handler.Dispose();
    }

    // ── Routing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePruneGoesThroughTheUntimedClient() {
        using var estate = TestClients.Create(pruneTimeout: TimeSpan.FromSeconds(30));

        await estate.Client.PruneImagesAsync(Ct);

        Assert.Empty(estate.Default.Requests);
        var request = Assert.Single(estate.LongRunning.Requests);
        Assert.Contains("/images/prune", request);
    }

    [Fact]
    public async Task TheContainerWaitGoesThroughTheUntimedClient() {
        using var estate = TestClients.Create(pruneTimeout: TimeSpan.FromSeconds(30));

        await estate.Client.WaitContainerAsync("abc123", Ct);

        Assert.Empty(estate.Default.Requests);
        var request = Assert.Single(estate.LongRunning.Requests);
        Assert.Contains("/containers/abc123/wait", request);
    }

    [Fact]
    public async Task AUiFacingCallStillGoesThroughTheDefaultClient() {
        using var estate = TestClients.Create(pruneTimeout: TimeSpan.FromSeconds(30));

        await estate.Client.ListContainersAsync(Ct);

        Assert.Empty(estate.LongRunning.Requests);
        var request = Assert.Single(estate.Default.Requests);
        Assert.Contains("/containers/json", request);
    }

    // ── The prune's own ceiling ───────────────────────────────────────────────

    [Fact]
    public async Task APruneThatOutlastsTheCapFailsAsATimeout_NotAsCancellation() {
        // A short injected cap standing in for the real 30 minutes; this daemon never answers.
        using var estate = TestClients.Create(pruneTimeout: TimeSpan.FromMilliseconds(50), hang: true);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => estate.Client.PruneImagesAsync(Ct));

        // ImagePruneBackgroundService.RunPruneAsync swallows OperationCanceledException as "we are
        // shutting down". A TimeoutException cannot match that filter, so a capped prune gets logged
        // rather than disappearing.
        Assert.False(typeof(OperationCanceledException).IsAssignableFrom(ex.GetType()));
        Assert.Contains("cap", ex.Message);
    }

    [Fact]
    public async Task ACancelledPruneStaysACancellation() {
        using var estate = TestClients.Create(pruneTimeout: TimeSpan.FromMinutes(30), hang: true);
        using var shutdown = new CancellationTokenSource();

        var prune = estate.Client.PruneImagesAsync(shutdown.Token);
        await shutdown.CancelAsync();

        // The caller's token, not the cap — the shutdown path in the background service has to keep
        // recognizing this one and stay quiet about it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prune);
    }

    // ── Doubles ───────────────────────────────────────────────────────────────

    /// <summary>Records the paths it is asked for and answers each with a parseable Docker body.</summary>
    private sealed class RecordingHandler(bool hang = false) : HttpMessageHandler {
        public List<string> Requests { get; } = [];
        public bool Disposed { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add(request.RequestUri!.PathAndQuery);
            // Stands in for a daemon that accepted the request and then went quiet — the shape the
            // client-side cap exists for.
            if (hang) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(BodyFor(path), Encoding.UTF8, "application/json"),
            };
        }

        // The wait response has a `required` StatusCode, so an empty object would not deserialize.
        private static string BodyFor(string path) =>
            path.EndsWith("/containers/json") ? "[]"
            : path.EndsWith("/wait") ? """{"StatusCode":0}"""
            : "{}";

        protected override void Dispose(bool disposing) {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    /// <summary>A client wired to two separate handlers, so which one a call took is observable.</summary>
    private sealed class ClientEstate : IDisposable {
        private readonly HttpClient _defaultClient;
        private readonly HttpClient _longRunningClient;

        public ClientEstate(
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

        public void Dispose() {
            Client.Dispose();
            _defaultClient.Dispose();
            _longRunningClient.Dispose();
            Default.Dispose();
            LongRunning.Dispose();
        }
    }

    private static class TestClients {
        public static ClientEstate Create(TimeSpan pruneTimeout, bool hang = false) {
            var defaultHandler = new RecordingHandler();
            var longRunningHandler = new RecordingHandler(hang);
            var baseAddress = new Uri("http://docker");
            var defaultClient = new HttpClient(defaultHandler, disposeHandler: false) { BaseAddress = baseAddress };
            var longRunningClient = new HttpClient(longRunningHandler, disposeHandler: false) {
                BaseAddress = baseAddress,
                Timeout = Timeout.InfiniteTimeSpan,
            };
            var client = new DockerEngineClient(ApiVersion, defaultClient, longRunningClient, pruneTimeout);
            return new ClientEstate(client, defaultHandler, longRunningHandler, defaultClient, longRunningClient);
        }
    }
}
