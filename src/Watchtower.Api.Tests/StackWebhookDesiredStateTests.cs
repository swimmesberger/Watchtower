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
public sealed class StackWebhookDesiredStateTests {
    [Fact]
    public async Task TheWebhookRefusesAStoppedStack_AndEnqueuesNothing() {
        using var factory = new WatchtowerApiFactory();
        var stackId = await SeedStackAsync(factory, StackDesiredState.Stopped);
        using var client = factory.CreateApiClient();

        var response = await client.PostAsync(
            $"/api/webhooks/stacks/{stackId}/deploy", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(factory.DeployQueue.Calls);
    }

    [Fact]
    public async Task TheWebhookStillDeploysARunningStack() {
        using var factory = new WatchtowerApiFactory();
        var stackId = await SeedStackAsync(factory, StackDesiredState.Running);
        using var client = factory.CreateApiClient();

        var response = await client.PostAsync(
            $"/api/webhooks/stacks/{stackId}/deploy", content: null, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal([(stackId, "webhook")], factory.DeployQueue.Calls);
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
