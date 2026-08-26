using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Tests;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// Covers the deploy webhook against stack desired state (ADR-0025): a CI push must not revive a
/// stack an operator deliberately stopped, while a running stack keeps deploying as before.
/// </summary>
/// <remarks>
/// Both stacks carry a webhook token and both calls present it: since the ADR-0026 retrofit an enabled
/// webhook without one refuses every call, so a tokenless fixture would be testing the bearer check
/// instead of the desired-state rule these tests are about (that check has its own suite,
/// <see cref="StackWebhookAuthTests"/>).
/// </remarks>
public sealed class StackWebhookDesiredStateTests {
    private const string Token = "desired-state-webhook-token";

    [Fact]
    public async Task TheWebhookRefusesAStoppedStack_AndEnqueuesNothing() {
        using var factory = new WatchtowerApiFactory();
        var stackId = await SeedStackAsync(factory, StackDesiredState.Stopped);
        using var client = factory.CreateApiClient();

        var response = await client.SendAsync(Deploy(stackId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.DeployQueue.Calls);
    }

    [Fact]
    public async Task TheWebhookStillDeploysARunningStack() {
        using var factory = new WatchtowerApiFactory();
        var stackId = await SeedStackAsync(factory, StackDesiredState.Running);
        using var client = factory.CreateApiClient();

        var response = await client.SendAsync(Deploy(stackId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal([(stackId, "webhook")], factory.DeployQueue.Calls);
    }

    private static HttpRequestMessage Deploy(int stackId) {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/stacks/{stackId}/deploy");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");
        return request;
    }

    private static async Task<int> SeedStackAsync(WatchtowerApiFactory factory, StackDesiredState desiredState) {
        var stackId = 0;
        await factory.WithScopeAsync(async services => {
            var db = services.GetRequiredService<WatchtowerDbContext>();
            var stack = new Stack {
                Name = "shop",
                ComposeProjectName = "shop",
                Product = TestProducts.New("shop", "https://github.com/acme/shop.git"),
                WebhookEnabled = true,
                WebhookToken = Token,
                DesiredState = desiredState,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            stackId = stack.Id;
        });
        return stackId;
    }
}
