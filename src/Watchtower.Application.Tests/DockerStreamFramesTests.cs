using System.Text;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the demux <see cref="DockerStreamFrames"/> lifted out of the log stream and now shared
/// with exec. Everything here is about bodies that do not arrive in tidy pieces: a socket hands out
/// whatever has landed, so a frame header split across two reads, an empty flush, or a body that
/// stops mid-frame are all ordinary — and each of them silently loses output if mishandled.
/// </summary>
public sealed class DockerStreamFramesTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<List<DockerStreamFrame>> ReadAllAsync(Stream stream) {
        var frames = new List<DockerStreamFrame>();
        await foreach (var frame in DockerStreamFrames.ReadAsync(stream, Ct)) frames.Add(frame);
        return frames;
    }

    private static string Text(DockerStreamFrame frame) => Encoding.UTF8.GetString(frame.Payload);

    [Fact]
    public async Task KeepsEachFramesStreamTypeAndOrder() {
        using var body = new MemoryStream(DockerFrameBuilder.Concat(
            DockerFrameBuilder.Frame(DockerStreamFrame.Stdout, "one"),
            DockerFrameBuilder.Frame(DockerStreamFrame.Stderr, "warning"),
            DockerFrameBuilder.Frame(DockerStreamFrame.Stdout, "two")));

        var frames = await ReadAllAsync(body);

        Assert.Collection(frames,
            f => {
                Assert.Equal(DockerStreamFrame.Stdout, f.StreamType);
                Assert.Equal("one", Text(f));
            },
            f => {
                Assert.Equal(DockerStreamFrame.Stderr, f.StreamType);
                Assert.Equal("warning", Text(f));
            },
            f => {
                Assert.Equal(DockerStreamFrame.Stdout, f.StreamType);
                Assert.Equal("two", Text(f));
            });
    }

    [Fact]
    public async Task SkipsAnEmptyFlushWithoutEndingTheStream() {
        // A zero-length frame is a flush with nothing buffered. Treating it as end-of-stream would
        // drop every frame behind it.
        using var body = new MemoryStream(DockerFrameBuilder.Concat(
            DockerFrameBuilder.EmptyFrame(DockerStreamFrame.Stdout),
            DockerFrameBuilder.Frame(DockerStreamFrame.Stdout, "after the flush")));

        var frames = await ReadAllAsync(body);

        var frame = Assert.Single(frames);
        Assert.Equal("after the flush", Text(frame));
    }

    [Fact]
    public async Task ReassemblesAHeaderAndPayloadSplitAcrossReads() {
        var whole = DockerFrameBuilder.Concat(
            DockerFrameBuilder.Frame(DockerStreamFrame.Stdout, "split down the middle"),
            DockerFrameBuilder.Frame(DockerStreamFrame.Stderr, "and this one too"));
        // Cuts land inside the first header, inside the first payload, and inside the second header.
        using var body = new ChunkedStream(
            whole[..3], whole[3..12], whole[12..31], whole[31..], []);

        var frames = await ReadAllAsync(body);

        Assert.Collection(frames,
            f => Assert.Equal("split down the middle", Text(f)),
            f => Assert.Equal("and this one too", Text(f)));
    }

    [Fact]
    public async Task StopsAtAFrameThePayloadOfWhichNeverArrived() {
        var complete = DockerFrameBuilder.Frame(DockerStreamFrame.Stdout, "all there");
        var cutOff = DockerFrameBuilder.Frame(DockerStreamFrame.Stdout, "never finished");
        using var body = new MemoryStream(DockerFrameBuilder.Concat(complete, cutOff[..12]));

        var frames = await ReadAllAsync(body);

        // What arrived whole is kept; the half-frame is not guessed at. Whether that mattered is
        // for the caller's exit code to say, not for this reader.
        var frame = Assert.Single(frames);
        Assert.Equal("all there", Text(frame));
    }

    [Fact]
    public async Task StopsWhenTheHeaderItselfIsCutShort() {
        using var body = new MemoryStream([1, 0, 0, 0, 0]);

        Assert.Empty(await ReadAllAsync(body));
    }

    [Fact]
    public async Task EndsOnAnEmptyBody() {
        using var body = new MemoryStream([]);

        Assert.Empty(await ReadAllAsync(body));
    }

    [Fact]
    public async Task ReadExactAsync_FillsTheBufferFromAsManyReadsAsItTakes() {
        using var stream = new ChunkedStream([1, 2], [3], [4, 5, 6]);
        var buffer = new byte[6];

        var read = await DockerStreamFrames.ReadExactAsync(stream, buffer, 6, Ct);

        Assert.Equal(6, read);
        Assert.Equal<byte[]>([1, 2, 3, 4, 5, 6], buffer);
    }

    [Fact]
    public async Task ReadExactAsync_ReturnsWhatItGotWhenTheStreamEndsEarly() {
        using var stream = new ChunkedStream([1, 2, 3]);
        var buffer = new byte[8];

        var read = await DockerStreamFrames.ReadExactAsync(stream, buffer, 8, Ct);

        Assert.Equal(3, read);
    }
}
