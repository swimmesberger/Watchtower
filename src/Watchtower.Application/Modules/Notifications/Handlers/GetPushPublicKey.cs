using Watchtower.Application.Modules.Notifications.WebPush;

namespace Watchtower.Application.Modules.Notifications.Handlers;

/// <summary>
/// The VAPID public key a browser subscribes with (<c>applicationServerKey</c>). Generates the instance's
/// key pair on first call when none is configured.
/// </summary>
[Handler("notifications.publicKey")]
public sealed class GetPushPublicKey(IVapidKeyProvider keys)
    : IHandler<GetPushPublicKey.Query, Result<GetPushPublicKey.Response>> {
    public sealed record Query : IQuery;

    /// <param name="PublicKey">Uncompressed P-256 point, base64url without padding.</param>
    public sealed record Response(string PublicKey);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) =>
        new Response((await keys.GetAsync(ct)).PublicKey);
}
