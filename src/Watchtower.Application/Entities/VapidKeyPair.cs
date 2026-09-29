namespace Watchtower.Application.Entities;

/// <summary>
/// The instance's VAPID key pair (ADR-0041, RFC 8292) — a single row, <see cref="SingletonId"/>. Generated
/// on first use when <c>Watchtower:WebPush</c> pins no pair, so a fresh installation can send
/// notifications without setup; the public half is what every browser subscribes with.
/// </summary>
/// <remarks>
/// A row rather than a file for the same reason as the other keys since ADR-0024: every instance must
/// sign with the key the browsers subscribed to, whichever instance they happened to reach. The private
/// half is stored through <c>KeyProtector</c> like every other private key in the database, marked by
/// <see cref="Protection"/>. The single fixed id is the race guard: two instances generating on the same
/// first use both insert id 1, one wins, and the loser re-reads the winner's pair.
/// </remarks>
public sealed class VapidKeyPair {
    /// <summary>The only id this table ever holds.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>The 65-byte uncompressed P-256 public point, base64url without padding.</summary>
    public required string PublicKey { get; set; }

    /// <summary>The 32-byte private scalar (as base64url text), protected per <see cref="Protection"/>.</summary>
    public required byte[] PrivateKey { get; set; }

    /// <summary>How <see cref="PrivateKey"/> is encoded — see <c>KeyProtector</c>.</summary>
    public required string Protection { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
