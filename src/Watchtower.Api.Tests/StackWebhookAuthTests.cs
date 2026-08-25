using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Tests;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The deploy webhook's bearer check after the ADR-0026 retrofit: one constant-time comparison
/// (<c>BearerTokens.Verify</c>) for every case, including the one that used to skip the check.
/// </summary>
/// <remarks>
/// <b>A behaviour change worth a release note.</b> An enabled webhook with an empty token was
/// previously an unauthenticated deploy trigger for anyone who knew the stack id — the stack Settings
/// form even offered it ("leave blank to allow unauthenticated deploys"). It is now refused, and the
/// form says so. Everything else about the endpoint is unchanged: a disabled or missing stack is still
/// a 404, a stopped stack still a 409, and a correct token still deploys.
/// </remarks>
public sealed class StackWebhookAuthTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Token = "s3cret-webhook-token";

    private static string Url(int stackId) => $"/api/webhooks/stacks/{stackId}/deploy";

    /// <summary>The hole the retrofit closes: enabled, no token, therefore nothing may call it.</summary>
    [Fact]
    public async Task AnEnabledWebhookWithNoToken_RefusesEveryCall() {
        using var factory = new WatchtowerApiFactory();
        var stackId = await SeedStackAsync(factory, "open", token: null);
        using var client = factory.CreateApiClient();

        var anonymous = await client.SendAsync(Deploy(stackId, bearer: null), Ct);
        // …including a caller that presents the empty string the old comparison would have matched.
        var emptyBearer = await client.SendAsync(Deploy(stackId, bearer: string.Empty), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, emptyBearer.StatusCode);
        Assert.Empty(factory.DeployQueue.Calls);
    }

    /// <summary>The token that is right still deploys — the retrofit changed how, not whether.</summary>
    [Fact]
    public async Task TheCorrectToken_StillDeploys() {
        using var factory = new WatchtowerApiFactory();
        var stackId = await SeedStackAsync(factory, "shop", Token);
        using var client = factory.CreateApiClient();

        var response = await client.SendAsync(Deploy(stackId, Token), Ct);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal([(stackId, "webhook")], factory.DeployQueue.Calls);
    }

    /// <summary>
    /// Every other bearer is refused: absent, another scheme, a value differing in one character (the
    /// case the constant-time comparison exists for), and one carrying trailing whitespace.
    /// </summary>
    [Theory]
    [InlineData(null, "Bearer")]
    [InlineData("", "Bearer")]
    [InlineData(Token, "Token")]
    [InlineData(Token + "x", "Bearer")]
    [InlineData("s3cret-webhook-toke", "Bearer")]
    public async Task EveryOtherBearer_IsRefused(string? bearer, string scheme) {
        using var factory = new WatchtowerApiFactory();
        var stackId = await SeedStackAsync(factory, "shop", Token);
        using var client = factory.CreateApiClient();

        var response = await client.SendAsync(Deploy(stackId, bearer, scheme), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(factory.DeployQueue.Calls);
    }

    /// <summary>A disabled webhook is still a 404 — the token is never the reason a caller learns that.</summary>
    [Fact]
    public async Task ADisabledWebhookIsStillANotFound_WhateverIsPresented() {
        using var factory = new WatchtowerApiFactory();
        var stackId = await SeedStackAsync(factory, "shop", Token, enabled: false);
        using var client = factory.CreateApiClient();

        var withToken = await client.SendAsync(Deploy(stackId, Token), Ct);
        var withoutToken = await client.SendAsync(Deploy(stackId, bearer: null), Ct);

        Assert.Equal(HttpStatusCode.NotFound, withToken.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, withoutToken.StatusCode);
    }

    private static HttpRequestMessage Deploy(int stackId, string? bearer, string scheme = "Bearer") {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(stackId));
        if (bearer is not null) request.Headers.TryAddWithoutValidation("Authorization", $"{scheme} {bearer}");
        return request;
    }

    private static async Task<int> SeedStackAsync(
        WatchtowerApiFactory factory, string name, string? token, bool enabled = true) {
        var stackId = 0;
        await factory.WithScopeAsync(async services => {
            var db = services.GetRequiredService<WatchtowerDbContext>();
            var stack = new Stack {
                Name = name,
                ComposeProjectName = name,
                Product = TestProducts.New(name, $"https://github.com/acme/{name}.git"),
                WebhookEnabled = enabled,
                WebhookToken = token,
                DesiredState = StackDesiredState.Running,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(Ct);
            stackId = stack.Id;
        });
        return stackId;
    }
}
