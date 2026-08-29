using System.Net;
using System.Text;
using System.Text.Json;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// <c>PullImageAsync</c>, whose three failure modes all end in the same place: the self-update
/// recreates the container on the image it was already running, the badge says "Update available"
/// again a minute later, and nothing anywhere says why. The daemon answers 200 the moment it accepts
/// a pull and reports the outcome in the streamed body, so a drained body is a silent success; the
/// call is also long enough to outlive the 100-second client, and its credential only applies when
/// the auth object names the registry the daemon knows.
/// </summary>
public sealed class DockerImagePullTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Answers a pull with the given newline-delimited progress frames.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage?> PullStream(params string[] frames) =>
        request => request.RequestUri!.AbsolutePath.EndsWith("/images/create")
            ? new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(string.Join('\n', frames), Encoding.UTF8, "application/json"),
            }
            : null;

    // ── Routing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ThePullGoesThroughTheUntimedClient() {
        // A self-update image is hundreds of megabytes: on the default client the 100-second ceiling
        // abandons any pull slower than that, mid-download, with nothing wrong at either end.
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromSeconds(30));
        estate.LongRunning.Responder = PullStream("""{"status":"Download complete"}""");

        await estate.Client.PullImageAsync("ghcr.io/acme/api:latest", ct: Ct);

        Assert.Empty(estate.Default.Requests);
        var request = Assert.Single(estate.LongRunning.Requests);
        Assert.Contains("/images/create", request);
    }

    // ── The reference the daemon is asked for ─────────────────────────────────

    [Theory]
    [InlineData("ghcr.io/acme/api:v2", "ghcr.io%2Facme%2Fapi", "v2")]
    [InlineData("ghcr.io/acme/api", "ghcr.io%2Facme%2Fapi", "latest")]
    [InlineData("registry.example.com:5000/api:v2", "registry.example.com%3A5000%2Fapi", "v2")]
    [InlineData("nginx", "library%2Fnginx", "latest")]
    [InlineData("acme/api:v2", "acme%2Fapi", "v2")]
    public async Task TheReferenceIsSplitIntoRepositoryAndTag(string image, string fromImage, string tag) {
        // The registry port in the third case is the one a naive "split at the last colon" gets
        // wrong, turning the port into a tag and the pull into a 404.
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromSeconds(30));
        estate.LongRunning.Responder = PullStream("""{"status":"Download complete"}""");

        await estate.Client.PullImageAsync(image, ct: Ct);

        var request = Assert.Single(estate.LongRunning.Requests);
        Assert.Contains($"fromImage={fromImage}", request);
        Assert.Contains($"tag={tag}", request);
    }

    [Fact]
    public async Task AnUnusableReferenceIsRejectedBeforeTheDaemonIsCalled() {
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<InvalidOperationException>(() => estate.Client.PullImageAsync("  ", ct: Ct));

        Assert.Empty(estate.LongRunning.Requests);
    }

    // ── The credential ────────────────────────────────────────────────────────

    [Fact]
    public async Task TheAuthObjectNamesTheRegistryHost() {
        // Cutting at the last slash instead yields "ghcr.io/acme" — no registry by that name, so the
        // daemon ignores the credential and the private pull falls back to anonymous and fails.
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromSeconds(30));
        estate.LongRunning.Responder = PullStream("""{"status":"Download complete"}""");

        await estate.Client.PullImageAsync("ghcr.io/acme/api:v2", "robot", "s3cret", Ct);

        Assert.Equal("ghcr.io", AuthField(estate, "serveraddress"));
        Assert.Equal("robot", AuthField(estate, "username"));
        Assert.Equal("s3cret", AuthField(estate, "password"));
    }

    [Fact]
    public async Task ADockerHubPullNamesTheV1Index() {
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromSeconds(30));
        estate.LongRunning.Responder = PullStream("""{"status":"Download complete"}""");

        await estate.Client.PullImageAsync("acme/api:v2", "robot", "s3cret", Ct);

        Assert.Equal("https://index.docker.io/v1/", AuthField(estate, "serveraddress"));
    }

    [Fact]
    public async Task ATokenWithJsonPunctuationSurvivesIntact() {
        // Interpolating the token into a JSON string literal breaks the header outright on the
        // quotes and backslashes a registry token is perfectly entitled to contain.
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromSeconds(30));
        estate.LongRunning.Responder = PullStream("""{"status":"Download complete"}""");
        const string awkward = """a"b\c""";

        await estate.Client.PullImageAsync("ghcr.io/acme/api:v2", "robot", awkward, Ct);

        Assert.Equal(awkward, AuthField(estate, "password"));
    }

    [Fact]
    public async Task NoCredentialSendsNoAuthHeader() {
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromSeconds(30));
        estate.LongRunning.Responder = PullStream("""{"status":"Download complete"}""");

        await estate.Client.PullImageAsync("ghcr.io/acme/api:v2", ct: Ct);

        Assert.Null(estate.LongRunning.RegistryAuth);
    }

    private static string? AuthField(DockerClientEstate estate, string field) {
        var header = Assert.IsType<string>(estate.LongRunning.RegistryAuth);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(header)));
        return doc.RootElement.GetProperty(field).GetString();
    }

    // ── The outcome, which lives in the body and not in the status line ───────

    [Fact]
    public async Task AFailureInTheProgressStreamFailsThePull() {
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromSeconds(30));
        estate.LongRunning.Responder = PullStream(
            """{"status":"Pulling from acme/api","id":"v2"}""",
            """{"errorDetail":{"message":"unauthorized: authentication required"},"error":"unauthorized"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => estate.Client.PullImageAsync("ghcr.io/acme/api:v2", ct: Ct));

        // The fuller errorDetail message, not the one-word summary next to it.
        Assert.Contains("authentication required", ex.Message);
        Assert.Contains("ghcr.io/acme/api:v2", ex.Message);
    }

    [Fact]
    public async Task ASuccessfulPullReadsTheWholeStream() {
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromSeconds(30));
        estate.LongRunning.Responder = PullStream(
            """{"status":"Pulling from acme/api","id":"v2"}""",
            """{"status":"Pulling fs layer","progressDetail":{}}""",
            """{"status":"Status: Downloaded newer image for ghcr.io/acme/api:v2"}""");

        await estate.Client.PullImageAsync("ghcr.io/acme/api:v2", ct: Ct);
    }

    [Fact]
    public async Task ARejectedRequestCarriesTheDaemonsMessage() {
        using var estate = DockerClientEstate.Create(pruneTimeout: TimeSpan.FromSeconds(30));
        estate.LongRunning.Responder = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError) {
            Content = new StringContent("""{"message":"no such host"}""", Encoding.UTF8, "application/json"),
        };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => estate.Client.PullImageAsync("ghcr.io/acme/api:v2", ct: Ct));

        Assert.Contains("no such host", ex.Message);
    }

    // ── Frame classification ──────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"errorDetail":{"message":"manifest unknown"},"error":"manifest unknown"}""", "manifest unknown")]
    [InlineData("""{"error":"toomanyrequests"}""", "toomanyrequests")]
    // errorDetail without a usable message falls through to the summary rather than to nothing.
    [InlineData("""{"errorDetail":{},"error":"denied"}""", "denied")]
    public void AFailureFrameYieldsItsMessage(string frame, string expected) =>
        Assert.Equal(expected, DockerEngineClient.ExtractPullError(frame));

    [Theory]
    [InlineData("""{"status":"Downloading","progressDetail":{"current":1,"total":2}}""")]
    [InlineData("""{"error":""}""")]
    [InlineData("")]
    [InlineData("   ")]
    // Not JSON, and not evidence of anything: a pull is not failed because a frame was unreadable.
    [InlineData("not json at all")]
    [InlineData("[]")]
    public void AProgressFrameYieldsNoError(string frame) =>
        Assert.Null(DockerEngineClient.ExtractPullError(frame));
}
