using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;

namespace Watchtower.Application.Modules.Notifications.WebPush;

/// <summary>
/// The send loop: one message to a list of subscriptions, best-effort per subscription, and cleanup of
/// the subscriptions that can never be delivered to again.
/// </summary>
/// <remarks>
/// <para>
/// TODO(elarion#162): generic delivery an Elarion Web Push package is expected to own; callers here
/// already speak only in subscriptions and <see cref="WebPushMessage"/>s so the swap is local.
/// </para>
/// <para>
/// Expired (404/410) and rejected (unusable keys) subscriptions are deleted after the loop; a transient
/// failure is logged and the subscription kept for next time. An exception from the client itself — the
/// VAPID pair is unusable, the message is invalid — is not about any one subscription, so it is logged
/// and nothing is deleted for it. Only the caller's own cancellation stops the loop.
/// </para>
/// </remarks>
public sealed class WebPushSender(
    IPushServiceClient client,
    IVapidKeyProvider vapidKeys,
    IPushSubscriptionStore store,
    ILogger<WebPushSender> logger) {
    /// <summary>Sends <paramref name="message"/> to every subscription; returns how many the push services accepted.</summary>
    public async ValueTask<int> SendAsync(
        IReadOnlyList<PushSubscription> subscriptions, WebPushMessage message, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(message);
        if (subscriptions.Count == 0) return 0;

        if (message.Topic is not null && !WebPushMessage.IsValidTopic(message.Topic)) {
            // A malformed topic only loses the collapse behaviour; failing the send for it would lose the
            // notification.
            logger.LogWarning("Push topic {Topic} is not a valid RFC 8030 topic; sending without one.", message.Topic);
            message = message with { Topic = null };
        }

        var vapid = await vapidKeys.GetAsync(ct);
        var delivered = 0;
        List<Guid>? dead = null;
        foreach (var subscription in subscriptions) {
            WebPushDeliveryResult result;
            try {
                result = await client.DeliverAsync(
                    new WebPushTarget(subscription.Endpoint, subscription.P256dh, subscription.Auth), message, vapid, ct);
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                logger.LogWarning(ex, "Push delivery to subscription {SubscriptionId} failed.", subscription.Id);
                continue;
            }

            switch (result.Status) {
                case WebPushDeliveryStatus.Delivered:
                    delivered++;
                    break;
                case WebPushDeliveryStatus.Expired:
                    (dead ??= []).Add(subscription.Id);
                    logger.LogInformation(
                        "Push subscription {SubscriptionId} is gone ({Status}); removing it.", subscription.Id, result.HttpStatus);
                    break;
                case WebPushDeliveryStatus.Rejected:
                    (dead ??= []).Add(subscription.Id);
                    logger.LogWarning(
                        result.Error, "Push subscription {SubscriptionId} has unusable keys; removing it.", subscription.Id);
                    break;
                default:
                    logger.LogWarning(
                        result.Error, "Push delivery to subscription {SubscriptionId} failed ({Status}); keeping it.",
                        subscription.Id, result.HttpStatus);
                    break;
            }
        }

        if (dead is not null) await store.RemoveAsync(dead, ct);
        return delivered;
    }
}
