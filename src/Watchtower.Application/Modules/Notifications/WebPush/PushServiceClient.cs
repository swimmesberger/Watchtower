using System.Net;
using System.Security.Cryptography;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using LibPushServiceClient = Lib.Net.Http.WebPush.PushServiceClient;
using LibPushSubscription = Lib.Net.Http.WebPush.PushSubscription;

namespace Watchtower.Application.Modules.Notifications.WebPush;

/// <summary>
/// The wire half of Web Push: encrypt one message to one subscription (RFC 8291), sign it (RFC 8292) and
/// hand it to the push service — reporting what happened to the <em>subscription</em> rather than
/// throwing for the outcomes the caller has to act on.
/// </summary>
/// <remarks>
/// TODO(elarion#162): an Elarion Web Push package would provide this transport; this interface exists so
/// the library behind it stays in one class and the send loop is testable with a fake.
/// </remarks>
public interface IPushServiceClient {
    /// <summary>Delivers <paramref name="message"/> to <paramref name="target"/>.</summary>
    /// <exception cref="Exception">
    /// Only for failures that are not about this subscription — an unusable VAPID key pair, an invalid
    /// message — which the caller must not answer by deleting the subscription.
    /// </exception>
    ValueTask<WebPushDeliveryResult> DeliverAsync(
        WebPushTarget target, WebPushMessage message, VapidCredentials vapid, CancellationToken ct);
}

/// <summary>
/// <see cref="IPushServiceClient"/> over <c>Lib.Net.Http.WebPush</c> — the only type that references the
/// library. Singleton: one <see cref="HttpClient"/> for the process's push traffic.
/// </summary>
internal sealed class LibWebPushServiceClient : IPushServiceClient, IDisposable {
    private readonly HttpClient _http;
    private readonly LibPushServiceClient _client;

    public LibWebPushServiceClient() {
        // A generous but finite timeout: a push service that hangs must not hold the notification queue.
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _client = new LibPushServiceClient(_http) {
            // Never sleep through a 429's Retry-After inside a send loop — a skipped notification is
            // better than a queue stalled behind one rate-limited device.
            AutoRetryAfter = false,
        };
    }

    public async ValueTask<WebPushDeliveryResult> DeliverAsync(
        WebPushTarget target, WebPushMessage message, VapidCredentials vapid, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(vapid);

        // Everything that concerns the message or the VAPID identity is built — and the signature forced —
        // before the per-subscription try below, so a broken key pair surfaces as an exception to the
        // caller instead of being mistaken for thousands of broken subscriptions and deleting them all.
        var pushMessage = new PushMessage(message.Payload) {
            Topic = WebPushMessage.IsValidTopic(message.Topic) ? message.Topic : null,
            Urgency = message.Urgency switch {
                WebPushUrgency.VeryLow => PushMessageUrgency.VeryLow,
                WebPushUrgency.Low => PushMessageUrgency.Low,
                WebPushUrgency.High => PushMessageUrgency.High,
                _ => PushMessageUrgency.Normal,
            },
            TimeToLive = (int)Math.Clamp(message.TimeToLive.TotalSeconds, 0, int.MaxValue),
        };
        using var auth = new VapidAuthentication(vapid.PublicKey, vapid.PrivateKey) { Subject = vapid.Subject };
        if (Uri.TryCreate(target.Endpoint, UriKind.Absolute, out var endpoint))
            _ = auth.GetVapidSchemeAuthenticationHeaderValueParameter(endpoint.GetLeftPart(UriPartial.Authority));

        try {
            var subscription = new LibPushSubscription { Endpoint = target.Endpoint };
            subscription.SetKey(PushEncryptionKeyName.P256DH, target.P256dh);
            subscription.SetKey(PushEncryptionKeyName.Auth, target.Auth);
            await _client.RequestPushMessageDeliveryAsync(subscription, pushMessage, auth, ct);
            return WebPushDeliveryResult.Delivered;
        } catch (PushServiceClientException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) {
            return new WebPushDeliveryResult(WebPushDeliveryStatus.Expired, (int)ex.StatusCode, ex);
        } catch (PushServiceClientException ex) {
            return new WebPushDeliveryResult(WebPushDeliveryStatus.Failed, (int)ex.StatusCode, ex);
        } catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException) {
            // The subscription's keys are not base64url or not a P-256 point: the payload can never be
            // encrypted to it, so it is as dead as a 410.
            return new WebPushDeliveryResult(WebPushDeliveryStatus.Rejected, Error: ex);
        } catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested)) {
            // Network failure or the HttpClient's own timeout — not a cancellation anyone asked for.
            return new WebPushDeliveryResult(WebPushDeliveryStatus.Failed, Error: ex);
        }
    }

    public void Dispose() => _http.Dispose();
}
