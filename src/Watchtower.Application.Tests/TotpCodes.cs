using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace Watchtower.Application.Tests;

/// <summary>
/// Produces the six digits an authenticator app would show for a given shared key — the test-side half of
/// the TOTP contract, standing in for the phone.
/// </summary>
/// <remarks>
/// Written out rather than borrowed because Identity's own generator (<c>Rfc6238AuthenticationService</c>)
/// is internal to <c>Microsoft.Extensions.Identity.Core</c>. That is what makes this worth having: an
/// independent implementation of RFC 6238 against the same key is a real check that
/// <c>AuthenticatorTokenProvider</c> is wired up and reading Watchtower's stored key correctly, whereas a
/// code produced by the verifier's own arithmetic would agree with it no matter how wrong both were.
/// <para>
/// The parameters are the ones <c>AuthenticatorTokenProvider</c> uses, which are also the interoperable
/// defaults every authenticator app assumes: HMAC-SHA1 over a 30-second step counted from the Unix epoch,
/// truncated to six digits, with the key Base32-decoded (RFC 4648).
/// </para>
/// <para>
/// <strong>Real time, not the test clock.</strong> The provider reads <c>DateTime.UtcNow</c> directly, so a
/// code has to be computed against the wall clock even in a host whose <c>TimeProvider</c> has been moved.
/// The verifier accepts a window of two steps either side, so a code stays good for about two and a half
/// minutes — long enough that no test needs to care about landing on a step boundary.
/// </para>
/// </remarks>
internal static class TotpCodes {
    /// <summary>RFC 4648 Base32 alphabet, matching Identity's <c>Base32</c> encoder.</summary>
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>The code an authenticator holding <paramref name="sharedKey"/> shows right now.</summary>
    public static string Current(string sharedKey) => At(sharedKey, DateTimeOffset.UtcNow);

    /// <summary>The code for the 30-second step containing <paramref name="when"/>.</summary>
    public static string At(string sharedKey, DateTimeOffset when) {
        ArgumentException.ThrowIfNullOrEmpty(sharedKey);

        var key = FromBase32(sharedKey);
        var counter = (ulong)(when.ToUnixTimeSeconds() / 30);

        Span<byte> counterBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(counterBytes, counter);

        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
        HMACSHA1.HashData(key, counterBytes, hash);

        // Dynamic truncation (RFC 4226 §5.3): the low nibble of the last byte picks the four-byte window.
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                     | ((hash[offset + 1] & 0xff) << 16)
                     | ((hash[offset + 2] & 0xff) << 8)
                     | (hash[offset + 3] & 0xff);

        return (binary % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>A code that is definitely wrong for <paramref name="sharedKey"/>, whatever the time is.</summary>
    /// <remarks>
    /// Derived by stepping the current code rather than hard-coded, so it cannot accidentally <em>be</em>
    /// the right answer on some unlucky run — which a fixed "000000" eventually would be.
    /// </remarks>
    public static string Wrong(string sharedKey) {
        var current = int.Parse(Current(sharedKey), CultureInfo.InvariantCulture);
        return ((current + 1) % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>RFC 4648 Base32 decode, mirroring Identity's <c>Base32.FromBase32</c>.</summary>
    private static byte[] FromBase32(string input) {
        var trimmed = input.TrimEnd('=').ToUpperInvariant();
        var output = new byte[trimmed.Length * 5 / 8];
        int bitIndex = 0, inputIndex = 0, outputBits = 0, outputIndex = 0;

        while (outputIndex < output.Length) {
            var value = Base32Alphabet.IndexOf(trimmed[inputIndex], StringComparison.Ordinal);
            if (value < 0) throw new FormatException($"'{trimmed[inputIndex]}' is not a Base32 character.");

            var bits = Math.Min(5 - bitIndex, 8 - outputBits);
            output[outputIndex] <<= bits;
            output[outputIndex] |= (byte)(value >> (5 - (bitIndex + bits)));
            bitIndex += bits;

            if (bitIndex >= 5) {
                inputIndex++;
                bitIndex = 0;
            }

            outputBits += bits;
            if (outputBits >= 8) {
                outputIndex++;
                outputBits = 0;
            }
        }

        return output;
    }
}
