using Elarion.Abstractions.Identity;
using Watchtower.Application.Modules.Notifications.WebPush;

namespace Watchtower.Application.Modules.Notifications.Handlers;

/// <summary>
/// Removes this browser's push subscription (notifications turned off in the app). Only the signed-in
/// operator's own row with that endpoint is deleted — knowing another device's endpoint is not a licence
/// to silence it.
/// </summary>
[Handler("notifications.unsubscribe")]
public sealed class UnsubscribePush(IPushSubscriptionStore store, ICurrentUser currentUser)
    : IHandler<UnsubscribePush.Command, Result<UnsubscribePush.Response>> {
    /// <param name="Endpoint">The endpoint the browser subscribed with.</param>
    public sealed record Command(string Endpoint);

    /// <param name="Removed">Whether a subscription was deleted; false is not an error (already gone).</param>
    public sealed record Response(bool Removed);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (string.IsNullOrEmpty(currentUser.UserId) || string.IsNullOrWhiteSpace(command.Endpoint))
            return new Response(false);
        return new Response(await store.DeleteAsync(currentUser.UserId, command.Endpoint.Trim(), ct));
    }
}
