using Elarion.Abstractions;
using Elarion.Abstractions.Identity;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Notifications;
using Watchtower.Application.Modules.Notifications.Contracts;
using Watchtower.Application.Modules.Notifications.Handlers;
using Watchtower.Application.Modules.Notifications.Services;
using Watchtower.Application.Modules.Notifications.WebPush;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The database-backed half of the Web Push notifications (ADR-0041): subscription ownership, the VAPID
/// key pair's first-use generation, and who counts as an operator when a deploy fails.
/// </summary>
public sealed class PushSubscriptionTests {
    private const string Endpoint = "https://push.example/send/abc";

    private static readonly Action<IServiceCollection> WithWebPush = NotificationsModule.AddInterimWebPush;

    [Fact]
    public async Task Subscribe_UpsertsByEndpoint_AndReassignsTheBrowserToWhoeverSubscribedLast() {
        var ct = TestContext.Current.CancellationToken;
        using var host = AuthTestHost.Start(WithWebPush);

        var first = await SubscribeAsync(host, "1", Endpoint, "key-a");
        var refreshed = await SubscribeAsync(host, "1", Endpoint, "key-b");
        Assert.Equal(first.Id, refreshed.Id);
        Assert.Equal(1, refreshed.SubscriptionCount);

        // Another account signs in on the same browser and turns notifications on: the row moves, it is
        // not duplicated — otherwise this device would get every notification twice, once per account.
        var reassigned = await SubscribeAsync(host, "2", Endpoint, "key-c");
        Assert.Equal(first.Id, reassigned.Id);
        Assert.Equal(1, reassigned.SubscriptionCount);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = Assert.Single(await db.PushSubscriptions.AsNoTracking().ToListAsync(ct));
        Assert.Equal("2", row.UserId);
        Assert.Equal("key-c", row.P256dh);
    }

    [Fact]
    public async Task Unsubscribe_DeletesOnlyTheCallersOwnRow() {
        using var host = AuthTestHost.Start(WithWebPush);
        await SubscribeAsync(host, "2", Endpoint, "key");

        Assert.False(await UnsubscribeAsync(host, "1", Endpoint));
        Assert.True(await UnsubscribeAsync(host, "2", Endpoint));
        Assert.False(await UnsubscribeAsync(host, "2", Endpoint));
    }

    [Theory]
    [InlineData("http://push.example/send/abc")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Subscribe_RefusesAnythingButAnHttpsEndpoint(string endpoint) {
        using var host = AuthTestHost.Start(WithWebPush);
        await using var scope = host.Services.CreateAsyncScope();
        var handler = new SubscribePush(
            scope.ServiceProvider.GetRequiredService<IPushSubscriptionStore>(), new TestUser("1"));

        var result = await handler.HandleAsync(
            new SubscribePush.Command(endpoint, "key", "auth"), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task VapidKeys_AreGeneratedOnce_Persisted_AndUsableForSigning() {
        var ct = TestContext.Current.CancellationToken;
        using var host = AuthTestHost.Start(WithWebPush);

        VapidCredentials first, second;
        await using (var scope = host.Services.CreateAsyncScope())
            first = await scope.ServiceProvider.GetRequiredService<IVapidKeyProvider>().GetAsync(ct);
        await using (var scope = host.Services.CreateAsyncScope())
            second = await scope.ServiceProvider.GetRequiredService<IVapidKeyProvider>().GetAsync(ct);

        Assert.Equal(first.PublicKey, second.PublicKey);
        Assert.Equal(first.PrivateKey, second.PrivateKey);
        Assert.Equal(WebPushOptions.DefaultSubject, first.Subject);
        // The pair must be in the exact encoding the library (and browsers) accept — signing proves it.
        using var auth = new VapidAuthentication(first.PublicKey, first.PrivateKey) { Subject = first.Subject };
        Assert.NotEmpty(auth.GetVapidSchemeAuthenticationHeaderValueParameter("https://push.example"));

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var row = Assert.Single(await db.VapidKeyPairs.AsNoTracking().ToListAsync(ct));
            Assert.Equal(first.PublicKey, row.PublicKey);
        }
    }

    [Fact]
    public async Task VapidKeys_ConfiguredPairWins() {
        var (publicKey, privateKey) = VapidKeyProvider.Generate();
        using var host = AuthTestHost.Start(
            WithWebPush,
            ("Watchtower:WebPush:PublicKey", publicKey),
            ("Watchtower:WebPush:PrivateKey", privateKey),
            ("Watchtower:WebPush:Subject", "mailto:ops@example.test"));
        await using var scope = host.Services.CreateAsyncScope();

        var keys = await scope.ServiceProvider.GetRequiredService<IVapidKeyProvider>()
            .GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(publicKey, keys.PublicKey);
        Assert.Equal("mailto:ops@example.test", keys.Subject);
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.VapidKeyPairs.AnyAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendToOperators_ReachesEnabledSystemRealmAccounts_AndDropsDeletedOnes(bool authEnabled) {
        var ct = TestContext.Current.CancellationToken;
        using var host = AuthTestHost.Start(WithWebPush);

        int operatorId, disabledId, tenantId;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var tenantRealm = new Realm { Name = "Tenant", Slug = "tenant", CreatedAt = DateTimeOffset.UtcNow };
            db.Realms.Add(tenantRealm);
            await db.SaveChangesAsync(ct);
            var op = AuthTestHost.NewUser("operator");
            var disabled = AuthTestHost.NewUser("disabled");
            disabled.Disabled = true;
            var tenant = AuthTestHost.NewUser("tenant-user");
            tenant.RealmId = tenantRealm.Id;
            db.Users.AddRange(op, disabled, tenant);
            await db.SaveChangesAsync(ct);
            (operatorId, disabledId, tenantId) = (op.Id, disabled.Id, tenant.Id);
        }

        await SubscribeAsync(host, ImplicitAdminCurrentUser.LocalUserId, "https://push.example/local", "k");
        await SubscribeAsync(host, operatorId.ToString(), "https://push.example/operator", "k");
        await SubscribeAsync(host, disabledId.ToString(), "https://push.example/disabled", "k");
        await SubscribeAsync(host, tenantId.ToString(), "https://push.example/tenant", "k");
        await SubscribeAsync(host, "999999", "https://push.example/deleted", "k");

        var client = new AcceptingClient();
        await using (var scope = host.Services.CreateAsyncScope()) {
            var sp = scope.ServiceProvider;
            var store = sp.GetRequiredService<IPushSubscriptionStore>();
            var notifier = new PushNotifier(
                store,
                new WebPushSender(client, sp.GetRequiredService<IVapidKeyProvider>(), store, NullLogger<WebPushSender>.Instance),
                sp.GetRequiredService<WatchtowerDbContext>(),
                new AuthStartupState(authEnabled),
                new TestUser(string.Empty),
                NullLogger<PushNotifier>.Instance);

            var delivered = await notifier.SendToOperatorsAsync(new PushNotification("t", "b"), ct);

            string[] expected = authEnabled
                ? ["https://push.example/operator"]
                : ["https://push.example/local", "https://push.example/operator"];
            Assert.Equal(expected.Length, delivered);
            Assert.Equal(expected, client.Endpoints.Order());
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var remaining = await db.PushSubscriptions.Select(s => s.Endpoint).ToListAsync(ct);
            // The deleted account's browser is gone; everyone else's is kept, operator or not.
            Assert.DoesNotContain("https://push.example/deleted", remaining);
            Assert.Equal(4, remaining.Count);
        }
    }

    // -- Helpers -------------------------------------------------------------------------------------

    private static async Task<SubscribePush.Response> SubscribeAsync(
        AuthTestHost host, string userId, string endpoint, string p256dh) {
        await using var scope = host.Services.CreateAsyncScope();
        var handler = new SubscribePush(
            scope.ServiceProvider.GetRequiredService<IPushSubscriptionStore>(), new TestUser(userId));
        var result = await handler.HandleAsync(
            new SubscribePush.Command(endpoint, p256dh, "auth", "test-agent"), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        return result.Value;
    }

    private static async Task<bool> UnsubscribeAsync(AuthTestHost host, string userId, string endpoint) {
        await using var scope = host.Services.CreateAsyncScope();
        var handler = new UnsubscribePush(
            scope.ServiceProvider.GetRequiredService<IPushSubscriptionStore>(), new TestUser(userId));
        var result = await handler.HandleAsync(new UnsubscribePush.Command(endpoint), TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);
        return result.Value.Removed;
    }

    private sealed class AcceptingClient : IPushServiceClient {
        public List<string> Endpoints { get; } = [];

        public ValueTask<WebPushDeliveryResult> DeliverAsync(
            WebPushTarget target, WebPushMessage message, VapidCredentials vapid, CancellationToken ct) {
            Endpoints.Add(target.Endpoint);
            return ValueTask.FromResult(WebPushDeliveryResult.Delivered);
        }
    }

    private sealed class TestUser(string userId) : ICurrentUser {
        public string UserId => userId;
        public string? Email => null;
        public IReadOnlyList<string> Roles => [];
        public bool IsAuthenticated => userId.Length > 0;
        public bool IsInRole(string role) => false;
        public bool HasClaim(string type, string value) => false;
        public IEnumerable<string> GetClaimValues(string type) => [];
    }
}
