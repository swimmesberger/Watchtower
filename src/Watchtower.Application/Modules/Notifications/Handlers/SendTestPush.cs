using Watchtower.Application.Modules.Notifications.Contracts;

namespace Watchtower.Application.Modules.Notifications.Handlers;

/// <summary>
/// Sends a test notification to the signed-in operator's own browsers — the "does my phone get these?"
/// button. <c>delivered</c> counts what the push services accepted; whether the device shows it is up to
/// the device (permissions, focus modes, iOS needing the app on the home screen).
/// </summary>
[Handler("notifications.test")]
public sealed class SendTestPush(IPushNotifier notifier)
    : IHandler<SendTestPush.Command, Result<SendTestPush.Response>> {
    public sealed record Command;

    /// <param name="Delivered">How many of the operator's subscriptions the push services accepted it for.</param>
    public sealed record Response(int Delivered);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var delivered = await notifier.SendToCurrentUserAsync(new PushNotification(
            Title: "Watchtower notifications work",
            Body: "This device will be told when a deploy fails.",
            Url: "/",
            Tag: "watchtower-test"), ct);
        return new Response(delivered);
    }
}
