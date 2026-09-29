using System.Globalization;
using System.Text.Json;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Notifications.Contracts;
using Watchtower.Application.Modules.Notifications.WebPush;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Notifications.Services;

/// <summary>
/// Watchtower's audiences on top of the generic Web Push seams: who counts as an operator, and how a
/// <see cref="PushNotification"/> becomes a message.
/// </summary>
[Service]
internal sealed class PushNotifier(
    IPushSubscriptionStore store,
    WebPushSender sender,
    WatchtowerDbContext db,
    AuthStartupState auth,
    ICurrentUser currentUser,
    ILogger<PushNotifier> logger) : IPushNotifier {
    /// <summary>
    /// How long a push service holds a notification for an offline device. Half a day: a deploy failure
    /// is still worth knowing about after a night with the phone off, but not after a week.
    /// </summary>
    private static readonly TimeSpan TimeToLive = TimeSpan.FromHours(12);

    public async ValueTask<int> SendToOperatorsAsync(PushNotification notification, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(notification);
        var recipients = await ResolveOperatorsAsync(ct);
        if (recipients.Count == 0) return 0;
        var subscriptions = await store.ListForUsersAsync(recipients, ct);
        return await sender.SendAsync(subscriptions, ToMessage(notification), ct);
    }

    public async ValueTask<int> SendToCurrentUserAsync(PushNotification notification, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(notification);
        if (string.IsNullOrEmpty(currentUser.UserId)) return 0;
        var subscriptions = await store.ListForUsersAsync([currentUser.UserId], ct);
        return await sender.SendAsync(subscriptions, ToMessage(notification), ct);
    }

    /// <summary>
    /// The owners, among those holding a subscription, who are operators right now. Evaluated per send
    /// rather than at subscribe time, so disabling an account or moving it out of the system realm stops
    /// its notifications immediately without anyone having to find its devices.
    /// </summary>
    private async Task<List<string>> ResolveOperatorsAsync(CancellationToken ct) {
        var owners = await store.ListOwnersAsync(ct);
        var audience = OperatorAudience.Classify(owners, auth.Enabled);

        var recipients = new List<string>();
        if (audience.IncludesLocal) recipients.Add(ImplicitAdminCurrentUser.LocalUserId);
        if (audience.AccountIds.Count == 0) return recipients;

        var ids = audience.AccountIds.Keys.ToList();
        var accounts = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Disabled, u.RealmId })
            .ToListAsync(ct);
        recipients.AddRange(accounts
            .Where(u => !u.Disabled && u.RealmId == Realm.SystemRealmId)
            .Select(u => audience.AccountIds[u.Id]));

        // An owner id that names no account any more: the account was deleted, and its browsers can never
        // be notified again. Dropped here, opportunistically, since nothing cascades to this table.
        var orphaned = audience.AccountIds
            .Where(pair => accounts.All(u => u.Id != pair.Key))
            .Select(pair => pair.Value)
            .ToList();
        if (orphaned.Count > 0) {
            var removed = await store.RemoveForUsersAsync(orphaned, ct);
            logger.LogInformation("Removed {Count} push subscription(s) of deleted accounts.", removed);
        }
        return recipients;
    }

    private static WebPushMessage ToMessage(PushNotification notification) {
        var payload = JsonSerializer.Serialize(
            new PushPayload(notification.Title, notification.Body, notification.Url, notification.Tag),
            NotificationsJsonContext.Default.PushPayload);
        // High urgency: every notification this module sends today is something an operator should act on,
        // so a dozing phone should wake for it.
        return new WebPushMessage(payload, notification.Tag, WebPushUrgency.High, TimeToLive);
    }
}

/// <summary>
/// Sorts subscription owner ids into the two kinds of operator identity. Pure, so the rule is testable
/// without a database; <see cref="PushNotifier"/> then checks the account ids against the users table.
/// </summary>
internal static class OperatorAudience {
    /// <param name="IncludesLocal">
    /// Whether the implicit <c>local</c> operator is an audience member — only while authentication is off.
    /// With it on, a subscription made in the no-auth days belongs to nobody who can sign in; it is kept
    /// (not deleted) so switching authentication back off restores it.
    /// </param>
    /// <param name="AccountIds">Owner ids that are account ids, keyed by the parsed id, valued by the stored string.</param>
    internal sealed record Result(bool IncludesLocal, IReadOnlyDictionary<int, string> AccountIds);

    internal static Result Classify(IEnumerable<string> ownerIds, bool authEnabled) {
        var includesLocal = false;
        var accountIds = new Dictionary<int, string>();
        foreach (var owner in ownerIds) {
            if (string.Equals(owner, ImplicitAdminCurrentUser.LocalUserId, StringComparison.Ordinal)) {
                includesLocal |= !authEnabled;
                continue;
            }
            if (int.TryParse(owner, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                accountIds.TryAdd(id, owner);
            // Anything else is not an identity Watchtower issues; it is ignored rather than guessed at.
        }
        return new Result(includesLocal, accountIds);
    }
}

/// <summary>The JSON the service worker's <c>push</c> handler receives (camelCase via the module context).</summary>
public sealed record PushPayload(string Title, string Body, string? Url, string? Tag);
