using System.Runtime.CompilerServices;

namespace Watchtower.Application.Services;

/// <summary>
/// One frame of Docker's multiplexed stream format: which stream it came from plus its bytes.
/// </summary>
/// <param name="StreamType">
/// <see cref="Stdout"/> or <see cref="Stderr"/> (0 is stdin, which the daemon never sends back).
/// </param>
/// <param name="Payload">The frame's bytes, exactly as they arrived — never empty.</param>
internal readonly record struct DockerStreamFrame(byte StreamType, byte[] Payload) {
    /// <summary>Stream-type byte for frames the container wrote to stdout.</summary>
    public const byte Stdout = 1;
    /// <summary>Stream-type byte for frames the container wrote to stderr.</summary>
    public const byte Stderr = 2;
}

/// <summary>
/// Reads Docker's multiplexed stream format — an 8-byte header (stream type, three reserved bytes,
/// big-endian payload length) followed by that many payload bytes, repeated until the body ends.
/// Container logs and the output of a non-TTY exec share this framing, which is why the demux lives
/// here rather than inside either caller.
/// </summary>
/// <remarks>
/// A body that ends mid-frame simply stops the enumeration: the daemon closes the connection this
/// way when a container dies mid-write, and the truncation is not something this layer can repair.
/// Callers that care whether the output is complete — the exec path — decide that from the exit
/// code they inspect afterwards, not from the frames.
/// </remarks>
internal static class DockerStreamFrames {
    /// <summary>Length of the fixed header in front of every frame.</summary>
    private const int HeaderSize = 8;

    /// <summary>
    /// Yields frames from <paramref name="stream"/> until it ends. Zero-length frames (which the
    /// daemon emits on a flush with nothing buffered) are skipped rather than surfaced, and a
    /// header or payload cut short by the end of the body ends the enumeration.
    /// </summary>
    public static async IAsyncEnumerable<DockerStreamFrame> ReadAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken ct = default) {
        var header = new byte[HeaderSize];

        while (!ct.IsCancellationRequested) {
            var bytesRead = await ReadExactAsync(stream, header, HeaderSize, ct);
            if (bytesRead < HeaderSize) yield break;

            // Bytes 4–7 encode the frame payload size as a big-endian uint32.
            var frameSize =
                (header[4] << 24) | (header[5] << 16) | (header[6] << 8) | header[7];
            if (frameSize == 0) continue;

            var payload = new byte[frameSize];
            bytesRead = await ReadExactAsync(stream, payload, frameSize, ct);
            if (bytesRead < frameSize) yield break;

            yield return new DockerStreamFrame(header[0], payload);
        }
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes into <paramref name="buffer"/>. Returns the
    /// number of bytes read, which is less than <paramref name="count"/> only at end of stream —
    /// a single <see cref="Stream.ReadAsync(Memory{byte},CancellationToken)"/> is free to return
    /// fewer bytes than asked for, so a frame header can and does arrive split across reads.
    /// </summary>
    public static async Task<int> ReadExactAsync(
        Stream stream, byte[] buffer, int count, CancellationToken ct = default) {
        var offset = 0;
        while (offset < count) {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0) break; // EOF
            offset += read;
        }
        return offset;
    }
}
