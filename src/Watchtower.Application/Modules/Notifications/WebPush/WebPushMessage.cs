using System.Text.RegularExpressions;

namespace Watchtower.Application.Modules.Notifications.WebPush;

// TODO(elarion#162): the message/target/credential shapes below are the generic Web Push vocabulary
// an Elarion Web Push package would own; replace them with its types when it ships.

/// <summary>
/// One Web Push message, before per-subscription encryption: an opaque payload plus the RFC 8030 delivery
/// headers the push service honours.
/// </summary>
/// <param name="Payload">The text the service worker's <c>push</c> event receives (JSON by convention).</param>
/// <param name="Topic">
/// RFC 8030 <c>Topic</c>: a newer message with the same topic replaces an undelivered older one at the push
/// service. Must match <see cref="IsValidTopic"/>; an invalid one is dropped by the sender rather than
/// failing every delivery.
/// </param>
/// <param name="Urgency">RFC 8030 <c>Urgency</c> — whether a battery-saving device should wake for it.</param>
/// <param name="TimeToLive">How long the push service keeps the message while the device is offline.</param>
public sealed partial record WebPushMessage(
    string Payload,
    string? Topic,
    WebPushUrgency Urgency,
    TimeSpan TimeToLive) {
    /// <summary>
    /// Whether <paramref name="topic"/> is a legal RFC 8030 topic: at most 32 characters of the URL- and
    /// filename-safe base64 alphabet.
    /// </summary>
    public static bool IsValidTopic(string? topic) => topic is not null && TopicPattern().IsMatch(topic);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,32}$")]
    private static partial Regex TopicPattern();
}

/// <summary>RFC 8030 §5.3 urgency levels.</summary>
public enum WebPushUrgency {
    VeryLow,
    Low,
    Normal,
    High,
}

/// <summary>Where one message goes: a subscription's endpoint and the browser keys it is encrypted to.</summary>
/// <param name="Endpoint">The push service URL.</param>
/// <param name="P256dh">The browser's ECDH public key, base64url.</param>
/// <param name="Auth">The browser's authentication secret, base64url.</param>
public sealed record WebPushTarget(string Endpoint, string P256dh, string Auth);

/// <summary>The VAPID identity a message is signed with (RFC 8292).</summary>
/// <param name="PublicKey">Uncompressed P-256 public point, base64url.</param>
/// <param name="PrivateKey">P-256 private scalar, base64url. Plaintext — never log or persist this record.</param>
/// <param name="Subject">The <c>sub</c> claim: a <c>mailto:</c> or <c>https:</c> contact.</param>
public sealed record VapidCredentials(string PublicKey, string PrivateKey, string Subject) {
    /// <summary>Keeps the private key out of logs and debugger displays.</summary>
    public override string ToString() => $"VapidCredentials {{ PublicKey = {PublicKey}, Subject = {Subject} }}";
}

/// <summary>What became of one delivery attempt, as far as the subscription is concerned.</summary>
public enum WebPushDeliveryStatus {
    /// <summary>The push service accepted the message.</summary>
    Delivered,

    /// <summary>
    /// The push service no longer knows the subscription (404/410) — the browser unsubscribed, or the
    /// user revoked permission. It will never become deliverable again.
    /// </summary>
    Expired,

    /// <summary>
    /// The subscription's own keys are unusable (not base64url, not a P-256 point), so no message can
    /// ever be encrypted to it.
    /// </summary>
    Rejected,

    /// <summary>
    /// Anything else — the push service is down, rate-limiting, timing out. Transient as far as anyone
    /// can tell, so the subscription is kept.
    /// </summary>
    Failed,
}

/// <param name="Status">The outcome.</param>
/// <param name="HttpStatus">The push service's status code, when it answered with one.</param>
/// <param name="Error">The underlying failure, for the log line — never shown to a user.</param>
public sealed record WebPushDeliveryResult(WebPushDeliveryStatus Status, int? HttpStatus = null, Exception? Error = null) {
    public static readonly WebPushDeliveryResult Delivered = new(WebPushDeliveryStatus.Delivered);
}
