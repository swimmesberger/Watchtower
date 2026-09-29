using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Notifications.WebPush;

/// <summary>What a browser hands over when it subscribes, attributed to the user who asked.</summary>
public sealed record PushSubscriptionRegistration(
    string UserId, string Endpoint, string P256dh, string Auth, string? UserAgent);

/// <summary>Persistence for browser push subscriptions, keyed by endpoint and owned by a user id.</summary>
/// <remarks>
/// TODO(elarion#162): generic subscription storage an Elarion Web Push package is expected to own;
/// only the entity and the context are Watchtower's.
/// </remarks>
public interface IPushSubscriptionStore {
    /// <summary>
    /// Inserts the subscription, or refreshes the existing row with the same endpoint — whoever owned it
    /// before. Saves immediately.
    /// </summary>
    ValueTask<PushSubscription> UpsertAsync(PushSubscriptionRegistration registration, CancellationToken ct = default);

    /// <summary>Deletes <paramref name="userId"/>'s subscription with that endpoint; false if there was none.</summary>
    ValueTask<bool> DeleteAsync(string userId, string endpoint, CancellationToken ct = default);

    /// <summary>How many subscriptions (devices) <paramref name="userId"/> has.</summary>
    ValueTask<int> CountForUserAsync(string userId, CancellationToken ct = default);

    /// <summary>Every distinct owner that has at least one subscription.</summary>
    ValueTask<IReadOnlyList<string>> ListOwnersAsync(CancellationToken ct = default);

    /// <summary>The subscriptions of the given owners.</summary>
    ValueTask<IReadOnlyList<PushSubscription>> ListForUsersAsync(
        IReadOnlyCollection<string> userIds, CancellationToken ct = default);

    /// <summary>Deletes subscriptions by id (the ones a push service reported gone).</summary>
    ValueTask<int> RemoveAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>Deletes every subscription of the given owners.</summary>
    ValueTask<int> RemoveForUsersAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default);
}

/// <summary><see cref="IPushSubscriptionStore"/> over <see cref="WatchtowerDbContext"/>. Scoped, like the context.</summary>
internal sealed class EfPushSubscriptionStore(WatchtowerDbContext db, TimeProvider time) : IPushSubscriptionStore {
    public async ValueTask<PushSubscription> UpsertAsync(
        PushSubscriptionRegistration registration, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(registration);
        var now = time.GetUtcNow();
        // Looked up by endpoint alone, not by endpoint and owner: the endpoint identifies the browser,
        // and a browser that another account enabled notifications on earlier now belongs to whoever just
        // enabled them — re-stamping the row is also what keeps the unique index from refusing the insert.
        var subscription = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == registration.Endpoint, ct);
        if (subscription is null) {
            subscription = new PushSubscription {
                Id = Guid.CreateVersion7(),
                UserId = registration.UserId,
                Endpoint = registration.Endpoint,
                P256dh = registration.P256dh,
                Auth = registration.Auth,
                CreatedAt = now,
            };
            db.PushSubscriptions.Add(subscription);
        }
        subscription.UserId = registration.UserId;
        subscription.P256dh = registration.P256dh;
        subscription.Auth = registration.Auth;
        subscription.UserAgent = registration.UserAgent;
        subscription.LastSeenAt = now;
        await db.SaveChangesAsync(ct);
        return subscription;
    }

    public async ValueTask<bool> DeleteAsync(string userId, string endpoint, CancellationToken ct = default) =>
        await db.PushSubscriptions
            .Where(s => s.UserId == userId && s.Endpoint == endpoint)
            .ExecuteDeleteAsync(ct) > 0;

    public async ValueTask<int> CountForUserAsync(string userId, CancellationToken ct = default) =>
        await db.PushSubscriptions.CountAsync(s => s.UserId == userId, ct);

    public async ValueTask<IReadOnlyList<string>> ListOwnersAsync(CancellationToken ct = default) =>
        await db.PushSubscriptions.Select(s => s.UserId).Distinct().ToListAsync(ct);

    public async ValueTask<IReadOnlyList<PushSubscription>> ListForUsersAsync(
        IReadOnlyCollection<string> userIds, CancellationToken ct = default) {
        if (userIds.Count == 0) return [];
        return await db.PushSubscriptions.AsNoTracking()
            .Where(s => userIds.Contains(s.UserId))
            .ToListAsync(ct);
    }

    public async ValueTask<int> RemoveAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) {
        if (ids.Count == 0) return 0;
        return await db.PushSubscriptions.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync(ct);
    }

    public async ValueTask<int> RemoveForUsersAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default) {
        if (userIds.Count == 0) return 0;
        return await db.PushSubscriptions.Where(s => userIds.Contains(s.UserId)).ExecuteDeleteAsync(ct);
    }
}
