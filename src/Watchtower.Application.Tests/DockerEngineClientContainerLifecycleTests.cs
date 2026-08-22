using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The container lifecycle calls the backup's quiesce step relies on (ADR-0019): the stop with an
/// explicit grace (<c>?t=</c>), and pause/unpause. Routing is the whole contract — a stop that silently
/// dropped the timeout would put the 10 s daemon default back into every backup window, and a pause
/// sent to the wrong endpoint would stop (or do nothing to) a container the run then unpauses.
/// </summary>
public sealed class DockerEngineClientContainerLifecycleTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (DockerEngineClient Client, RecordingHandler Handler) Create() {
        var handler = new RecordingHandler();
        return (new DockerEngineClient("1.43", handler, TimeSpan.FromMinutes(30)), handler);
    }

    [Fact]
    public async Task StopWithoutATimeoutLeavesTheDaemonDefaultAlone() {
        var (client, handler) = Create();
        using (client) await client.StopContainerAsync("abc", Ct);

        Assert.Equal(["/v1.43/containers/abc/stop"], handler.Requests);
    }

    [Fact]
    public async Task StopWithATimeoutSendsItAsTheQueryParameter() {
        var (client, handler) = Create();
        using (client) await client.StopContainerAsync("abc", timeoutSeconds: 3, Ct);

        Assert.Equal(["/v1.43/containers/abc/stop?t=3"], handler.Requests);
    }

    [Fact]
    public async Task ANullTimeoutIsTheSameAsTheShortOverload() {
        var (client, handler) = Create();
        using (client) await client.StopContainerAsync("abc", timeoutSeconds: null, Ct);

        Assert.Equal(["/v1.43/containers/abc/stop"], handler.Requests);
    }

    [Fact]
    public async Task PauseAndUnpauseHitTheirOwnEndpoints() {
        var (client, handler) = Create();
        using (client) {
            await client.PauseContainerAsync("abc", Ct);
            await client.UnpauseContainerAsync("abc", Ct);
        }

        Assert.Equal(["/v1.43/containers/abc/pause", "/v1.43/containers/abc/unpause"], handler.Requests);
        // Both are plain POSTs with no body — what the daemon expects.
        Assert.All(handler.Bodies, body => Assert.Null(body));
    }
}
