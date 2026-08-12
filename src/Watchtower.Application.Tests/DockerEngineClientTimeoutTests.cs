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
    public async Task Dispose_DisposesTheHandlerItOwns() {
        // The production constructor builds the handler, so this instance has to dispose it — the
        // half of the invariant that leaks a handler and its socket pool if it regresses.
        using var handler = new RecordingHandler();
        var client = new DockerEngineClient(ApiVersion, handler, TimeSpan.FromMinutes(30));
        // Reach the clients through the class so the disposal below is observable on them too.
        await client.ListContainersAsync(Ct);

        client.Dispose();

        Assert.True(handler.Disposed);
        // Both clients went down with it: a request now fails as disposed rather than reaching
        // a handler that is no longer there.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ListContainersAsync(Ct));
    }

    [Fact]
    public async Task Dispose_LeavesABorrowedHandlerAlone() {
        using var handler = new RecordingHandler();
        var (defaultClient, longRunningClient) = DockerEngineClient.CreateClients(handler);
        var client = new DockerEngineClient(ApiVersion, defaultClient, longRunningClient, TimeSpan.FromMinutes(30));

        client.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => defaultClient.GetAsync("http://docker/_ping", Ct));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => longRunningClient.GetAsync("http://docker/_ping", Ct));
        // Clients supplied from outside bring their own handler ownership; this instance built
        // nothing and so disposes nothing.
        Assert.False(handler.Disposed);
    }

    // ── Routing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePruneGoesThroughTheUntimedClient() {
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromSeconds(30));

        await estate.Client.PruneImagesAsync(Ct);

        Assert.Empty(estate.Default.Requests);
        var request = Assert.Single(estate.LongRunning.Requests);
        Assert.Contains("/images/prune", request);
    }

    [Fact]
    public async Task TheContainerWaitGoesThroughTheUntimedClient() {
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromSeconds(30));

        await estate.Client.WaitContainerAsync("abc123", Ct);

        Assert.Empty(estate.Default.Requests);
        var request = Assert.Single(estate.LongRunning.Requests);
        Assert.Contains("/containers/abc123/wait", request);
    }

    [Fact]
    public async Task AUiFacingCallStillGoesThroughTheDefaultClient() {
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromSeconds(30));

        await estate.Client.ListContainersAsync(Ct);

        Assert.Empty(estate.LongRunning.Requests);
        var request = Assert.Single(estate.Default.Requests);
        Assert.Contains("/containers/json", request);
    }

    // ── The prune's own ceiling ───────────────────────────────────────────────

    [Fact]
    public async Task APruneThatOutlastsTheCapFailsAsATimeout() {
        // A short injected cap standing in for the real 30 minutes; this daemon never answers.
        using var estate = DockerClientEstate.Create(
            pruneTimeout: TimeSpan.FromMilliseconds(50), hangLongRunning: true);

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => estate.Client.PruneImagesAsync(Ct));

        Assert.Contains("cap", ex.Message);
    }

    [Fact]
    public async Task APruneCappedWhileTheCallerIsAlsoShuttingDownStillFailsAsATimeout() {
        // The cap fires first and the shutdown lands in the window before the catch filter runs —
        // the race that inferring "the cap must have fired" from the caller's token would lose.
        // Whether it was the cap is read off the cap's own source, so this stays a TimeoutException
        // instead of an OperationCanceledException the background loop swallows as routine.
        using var shutdown = new CancellationTokenSource();
        using var estate = DockerClientEstate.Create(
            pruneTimeout: TimeSpan.FromMilliseconds(50),
            hangLongRunning: true,
            onLongRunningCancelled: () => shutdown.Cancel());

        await Assert.ThrowsAsync<TimeoutException>(() => estate.Client.PruneImagesAsync(shutdown.Token));
        Assert.True(shutdown.IsCancellationRequested);
    }

    [Fact]
    public async Task ACancelledPruneStaysACancellation() {
        using var estate = DockerClientEstate.Create(
            pruneTimeout: TimeSpan.FromMinutes(30), hangLongRunning: true);
        using var shutdown = new CancellationTokenSource();

        var prune = estate.Client.PruneImagesAsync(shutdown.Token);
        await shutdown.CancelAsync();

        // The caller's token, not the cap — the shutdown path in the background service has to keep
        // recognizing this one and stay quiet about it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prune);
    }
}
