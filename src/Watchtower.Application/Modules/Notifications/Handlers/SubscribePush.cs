using Elarion.Abstractions.Identity;
using Watchtower.Application.Modules.Notifications.WebPush;

namespace Watchtower.Application.Modules.Notifications.Handlers;

/// <summary>
/// Registers — or refreshes — this browser's push subscription for the signed-in operator. Idempotent per
/// endpoint; a browser previously subscribed by another account now notifies this one.
/// </summary>
/// <remarks>
/// Operators only, like every handler here: the realm rule (<c>SystemRealmAuthorizer</c>) applies
/// centrally. The frontend calls this on every app start while notifications are on, which is what keeps
/// <c>lastSeenAt</c> meaningful and re-attaches a browser whose subscription the push service rotated.
/// </remarks>
[Handler("notifications.subscribe")]
public sealed class SubscribePush(IPushSubscriptionStore store, ICurrentUser currentUser)
    : IHandler<SubscribePush.Command, Result<SubscribePush.Response>> {
    /// <summary>The largest endpoint accepted — matches the column; real ones are a few hundred characters.</summary>
    internal const int MaxEndpointLength = 2048;

    /// <param name="Endpoint">The push service URL from <c>PushSubscription.endpoint</c>. Must be https.</param>
    /// <param name="P256dh">The browser's ECDH public key (<c>getKey("p256dh")</c>, base64url).</param>
    /// <param name="Auth">The browser's authentication secret (<c>getKey("auth")</c>, base64url).</param>
    /// <param name="UserAgent">Optional device description, only for telling devices apart.</param>
    public sealed record Command(string Endpoint, string P256dh, string Auth, string? UserAgent = null);

    /// <param name="Id">The subscription's id.</param>
    /// <param name="SubscriptionCount">How many browsers the signed-in operator now has subscribed.</param>
    public sealed record Response(Guid Id, int SubscriptionCount);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (string.IsNullOrEmpty(currentUser.UserId))
            return AppError.Unauthorized("Sign in to enable notifications.");

        var endpoint = command.Endpoint?.Trim() ?? string.Empty;
        // https only: the endpoint is where the server will POST, so anything else is either not a push
        // service or an attempt to point Watchtower's outbound requests somewhere it should not go.
        if (endpoint.Length is 0 or > MaxEndpointLength
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
            return AppError.Validation("The push endpoint must be an https URL of a browser push service.");
        if (string.IsNullOrWhiteSpace(command.P256dh) || command.P256dh.Length > 200)
            return AppError.Validation("The subscription's p256dh key is missing or too long.");
        if (string.IsNullOrWhiteSpace(command.Auth) || command.Auth.Length > 100)
            return AppError.Validation("The subscription's auth secret is missing or too long.");

        var userAgent = string.IsNullOrWhiteSpace(command.UserAgent) ? null : command.UserAgent.Trim();
        if (userAgent is { Length: > 500 }) userAgent = userAgent[..500];

        var subscription = await store.UpsertAsync(
            new PushSubscriptionRegistration(
                currentUser.UserId, endpoint, command.P256dh.Trim(), command.Auth.Trim(), userAgent),
            ct);
        var count = await store.CountForUserAsync(currentUser.UserId, ct);
        return new Response(subscription.Id, count);
    }
}
