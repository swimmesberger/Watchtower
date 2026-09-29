namespace Watchtower.Application.Modules.Notifications.Contracts;

/// <summary>
/// The Notifications module's published surface: send a Web Push notification to an audience's browsers
/// (ADR-0041). Delivery is best-effort — the result is how many subscriptions the push services accepted
/// the message for, and subscriptions they report as gone are removed on the way.
/// </summary>
[ModuleContract]
public interface IPushNotifier {
    /// <summary>
    /// Sends to every browser of every operator: accounts of the system realm that are not disabled, plus
    /// the implicit <c>local</c> operator while authentication is switched off. Needs no signed-in caller,
    /// so background work (the deploy-failure notifier) can use it.
    /// </summary>
    ValueTask<int> SendToOperatorsAsync(PushNotification notification, CancellationToken ct = default);

    /// <summary>Sends to the signed-in user's own browsers only — the test notification.</summary>
    ValueTask<int> SendToCurrentUserAsync(PushNotification notification, CancellationToken ct = default);
}

/// <summary>What a notification says. Never put secrets here: the payload leaves the machine.</summary>
/// <param name="Title">Notification title.</param>
/// <param name="Body">Notification body text.</param>
/// <param name="Url">In-app path the service worker opens on tap (e.g. <c>/stacks/12?event=345</c>).</param>
/// <param name="Tag">
/// Collapse key: a newer notification with the same tag replaces the older one on the device, and — sent
/// as the RFC 8030 topic — an undelivered older one at the push service. <c>[A-Za-z0-9_-]{1,32}</c>.
/// </param>
public sealed record PushNotification(string Title, string Body, string? Url = null, string? Tag = null);
