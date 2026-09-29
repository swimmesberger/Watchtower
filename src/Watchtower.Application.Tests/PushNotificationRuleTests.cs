using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Notifications.Services;
using Watchtower.Application.Modules.Notifications.WebPush;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The database-free half of the Web Push notifications (ADR-0041): which triggers count as automatic,
/// the spam guard built on that, what a deploy-failure notification may carry, who counts as an operator,
/// and the send loop's cleanup of subscriptions a push service reports as gone.
/// </summary>
public sealed class PushNotificationRuleTests {
    // -- DeployTriggers.IsAutomatic ---------------------------------------------------------------

    [Theory]
    [InlineData(DeployTriggers.AutoUpdate, true)]
    [InlineData(DeployTriggers.Schedule, true)]
    [InlineData(DeployTriggers.Release, true)]
    [InlineData(DeployTriggers.ReleaseReconcile, true)]
    [InlineData(DeployTriggers.Manual, false)]
    [InlineData(DeployTriggers.Webhook, false)]
    [InlineData(DeployTriggers.ReleaseManual, false)]
    [InlineData(DeployTriggers.VolumeRecreate, false)]
    // An unknown trigger counts as explicit: when in doubt, tell someone.
    [InlineData("something-new", false)]
    [InlineData("Release", false)]
    public void IsAutomatic_IsTrueOnlyForTheTriggersWatchtowerStartsItself(string trigger, bool expected) =>
        Assert.Equal(expected, DeployTriggers.IsAutomatic(trigger));

    // -- Spam guard ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(DeployTriggers.Manual, null, true)]
    [InlineData(DeployTriggers.Manual, "success", true)]
    // Explicit triggers notify even when the stack was already failing — somebody just asked.
    [InlineData(DeployTriggers.Manual, "failed", true)]
    [InlineData(DeployTriggers.Webhook, "failed", true)]
    [InlineData(DeployTriggers.ReleaseManual, "failed", true)]
    [InlineData(DeployTriggers.VolumeRecreate, "failed", true)]
    // Automatic triggers notify on the transition into failing only.
    [InlineData(DeployTriggers.Schedule, null, true)]
    [InlineData(DeployTriggers.Schedule, "success", true)]
    [InlineData(DeployTriggers.Schedule, "failed", false)]
    [InlineData(DeployTriggers.AutoUpdate, "failed", false)]
    [InlineData(DeployTriggers.Release, "failed", false)]
    [InlineData(DeployTriggers.ReleaseReconcile, "failed", false)]
    [InlineData(DeployTriggers.ReleaseReconcile, "success", true)]
    public void ShouldNotify_AppliesTheSpamGuardToAutomaticTriggersOnly(
        string trigger, string? previousTerminalStatus, bool expected) =>
        Assert.Equal(expected, DeployFailureNotificationPolicy.ShouldNotify(trigger, previousTerminalStatus));

    [Fact]
    public void Build_NamesTheStackAndLinksTheEvent_AndCollapsesPerStack() {
        var notification = DeployFailureNotificationPolicy.Build(12, "billing", 345, DeployTriggers.Schedule);

        Assert.Equal("Deploy failed: billing", notification.Title);
        Assert.Equal("Triggered by schedule · tap to view the log", notification.Body);
        Assert.Equal("/stacks/12?event=345", notification.Url);
        Assert.Equal("deploy-12", notification.Tag);
        // The tag doubles as the RFC 8030 topic, so it has to be a legal one or the collapse is lost.
        Assert.True(WebPushMessage.IsValidTopic(notification.Tag));
    }

    // -- Operator audience ---------------------------------------------------------------------------

    [Fact]
    public void Classify_IncludesTheLocalOperatorOnlyWhileAuthIsOff() {
        var owners = new[] { ImplicitAdminCurrentUser.LocalUserId, "7", "not-an-id", "-3", "0" };

        var off = OperatorAudience.Classify(owners, authEnabled: false);
        var on = OperatorAudience.Classify(owners, authEnabled: true);

        Assert.True(off.IncludesLocal);
        Assert.False(on.IncludesLocal);
        // Account ids are candidates either way; the users table decides whether they are operators.
        Assert.Equal([7], off.AccountIds.Keys);
        Assert.Equal([7], on.AccountIds.Keys);
        Assert.Equal("7", on.AccountIds[7]);
    }

    // -- WebPushSender -------------------------------------------------------------------------------

    [Fact]
    public async Task Sender_RemovesExpiredAndRejectedSubscriptions_AndKeepsTransientFailures() {
        var ct = TestContext.Current.CancellationToken;
        var delivered = NewSubscription("https://push.example/delivered");
        var gone = NewSubscription("https://push.example/gone");
        var broken = NewSubscription("https://push.example/broken");
        var flaky = NewSubscription("https://push.example/flaky");
        var throwing = NewSubscription("https://push.example/throwing");
        var client = new FakePushServiceClient(new Dictionary<string, Func<WebPushDeliveryResult>> {
            [delivered.Endpoint] = () => WebPushDeliveryResult.Delivered,
            [gone.Endpoint] = () => new WebPushDeliveryResult(WebPushDeliveryStatus.Expired, 410),
            [broken.Endpoint] = () => new WebPushDeliveryResult(WebPushDeliveryStatus.Rejected),
            [flaky.Endpoint] = () => new WebPushDeliveryResult(WebPushDeliveryStatus.Failed, 503),
            // Not about this subscription (e.g. a broken VAPID pair) — must never cost the subscription.
            [throwing.Endpoint] = () => throw new InvalidOperationException("VAPID key unusable"),
        });
        var store = new RecordingSubscriptionStore();
        var sender = new WebPushSender(client, new FixedVapidKeys(), store, NullLogger<WebPushSender>.Instance);

        var count = await sender.SendAsync(
            [delivered, gone, broken, flaky, throwing],
            new WebPushMessage("{}", "deploy-1", WebPushUrgency.High, TimeSpan.FromHours(1)),
            ct);

        Assert.Equal(1, count);
        Assert.Equal(5, client.Attempts.Count);
        Assert.Equal(new[] { gone.Id, broken.Id }.Order(), store.Removed.Order());
    }

    [Fact]
    public async Task Sender_DropsAnInvalidTopic_RatherThanTheNotification() {
        var ct = TestContext.Current.CancellationToken;
        var subscription = NewSubscription("https://push.example/a");
        var client = new FakePushServiceClient(new Dictionary<string, Func<WebPushDeliveryResult>> {
            [subscription.Endpoint] = () => WebPushDeliveryResult.Delivered,
        });
        var sender = new WebPushSender(
            client, new FixedVapidKeys(), new RecordingSubscriptionStore(), NullLogger<WebPushSender>.Instance);

        var count = await sender.SendAsync(
            [subscription], new WebPushMessage("{}", "not a topic!", WebPushUrgency.Normal, TimeSpan.Zero), ct);

        Assert.Equal(1, count);
        Assert.Null(Assert.Single(client.Attempts).Topic);
    }

    [Fact]
    public void DeployFailed_NeverBlocksTheDeployPath_EvenWhenNothingDrainsTheQueue() {
        using var notifier = new DeployFailureNotifier(
            new NoScopes(), NullLogger<DeployFailureNotifier>.Instance);

        // Far past capacity with the loop never started: every call returns, the oldest are dropped.
        for (var i = 0; i < DeployFailureNotifier.Capacity * 4; i++) notifier.DeployFailed(i);
    }

    // -- Doubles -------------------------------------------------------------------------------------

    private static PushSubscription NewSubscription(string endpoint) => new() {
        Id = Guid.CreateVersion7(),
        UserId = "1",
        Endpoint = endpoint,
        P256dh = "p256dh",
        Auth = "auth",
        CreatedAt = DateTimeOffset.UtcNow,
        LastSeenAt = DateTimeOffset.UtcNow,
    };

    private sealed class FakePushServiceClient(IReadOnlyDictionary<string, Func<WebPushDeliveryResult>> outcomes)
        : IPushServiceClient {
        public List<WebPushMessage> Attempts { get; } = [];

        public ValueTask<WebPushDeliveryResult> DeliverAsync(
            WebPushTarget target, WebPushMessage message, VapidCredentials vapid, CancellationToken ct) {
            Attempts.Add(message);
            return ValueTask.FromResult(outcomes[target.Endpoint]());
        }
    }

    private sealed class FixedVapidKeys : IVapidKeyProvider {
        public ValueTask<VapidCredentials> GetAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new VapidCredentials("public", "private", "https://example.test"));
    }

    private sealed class RecordingSubscriptionStore : IPushSubscriptionStore {
        public List<Guid> Removed { get; } = [];

        public ValueTask<int> RemoveAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) {
            Removed.AddRange(ids);
            return ValueTask.FromResult(ids.Count);
        }

        public ValueTask<PushSubscription> UpsertAsync(PushSubscriptionRegistration registration, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask<bool> DeleteAsync(string userId, string endpoint, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask<int> CountForUserAsync(string userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask<IReadOnlyList<string>> ListOwnersAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public ValueTask<IReadOnlyList<PushSubscription>> ListForUsersAsync(
            IReadOnlyCollection<string> userIds, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask<int> RemoveForUsersAsync(IReadOnlyCollection<string> userIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoScopes : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() =>
            throw new NotSupportedException("The loop is never started in this test.");
    }
}
