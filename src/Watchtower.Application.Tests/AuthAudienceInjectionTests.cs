using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <c>WATCHTOWER_AUTH_AUDIENCE</c> — the other half of the JWKS injection
/// (<see cref="AuthJwksInjectionTests"/>). The JWKS lets an app decide an assertion is genuine; the
/// audience lets it decide the assertion was minted for <em>it</em>, and not for another application
/// behind the same edge. The value differs per edge — a Cloudflare Access application's AUD tag, or
/// the route's own hostname under integrated auth — which is exactly why the app reads it from the
/// environment rather than hard-coding one.
/// </summary>
public sealed class AuthAudienceInjectionTests {
    private static AppApiTokens.RouteAudience Route(
        string domain, AccessMode mode = AccessMode.Authenticated, string? aud = null) =>
        new(domain, mode, aud);

    private static WatchtowerOptions Cloudflare() => new() {
        Proxy = new ProxyOptions { Enabled = true, Provider = "cloudflare" },
    };

    private static WatchtowerOptions Integrated() => new() {
        PublicBaseUrl = "https://wt.example.com",
        Auth = new AuthOptions { Enabled = true },
    };

    [Fact]
    public void CloudflareProvider_ResolvesTheAccessApplicationsAudTag() {
        var audience = AppApiTokens.ResolveAudience(
            Cloudflare(), [Route("app.example.com", aud: "  4714c1358e65fe6b  ")]);
        Assert.Equal("4714c1358e65fe6b", audience);
    }

    [Fact]
    public void IntegratedAuth_ResolvesTheRoutesOwnHostname() {
        // AuthTokenSigner.Mint stamps the route domain as `aud`, so nothing extra is stored — and a
        // recorded Cloudflare tag from a previous provider must not leak into this answer.
        var audience = AppApiTokens.ResolveAudience(
            Integrated(), [Route("app.example.com", aud: "4714c1358e65fe6b")]);
        Assert.Equal("app.example.com", audience);
    }

    [Fact]
    public void PublicRoutes_ContributeNothing() {
        // No gate in front of them, so no assertion to bind.
        Assert.Null(AppApiTokens.ResolveAudience(
            Cloudflare(), [Route("open.example.com", AccessMode.Public, aud: "deadbeef")]));
        Assert.Null(AppApiTokens.ResolveAudience(
            Integrated(), [Route("open.example.com", AccessMode.Public)]));
    }

    [Fact]
    public void SeveralProtectedRoutes_AreCommaJoinedInHostnameOrder() {
        var audience = AppApiTokens.ResolveAudience(Integrated(), [
            Route("zed.example.com"),
            Route("api.example.com", AccessMode.Restricted),
            Route("app.example.com"),
        ]);
        // Ordered so the generated compose override does not churn between two deploys that agree.
        Assert.Equal("api.example.com,app.example.com,zed.example.com", audience);
    }

    [Fact]
    public void ARouteTheProviderHasNotReconciledYet_ContributesNothing() {
        // Fail-closed: injecting a blank would give the app an audience that matches nothing anyway,
        // and injecting the other route's tag would tell it to accept an assertion minted elsewhere.
        var audience = AppApiTokens.ResolveAudience(Cloudflare(), [
            Route("app.example.com", aud: "4714c1358e65fe6b"),
            Route("pending.example.com", aud: null),
        ]);
        Assert.Equal("4714c1358e65fe6b", audience);
    }

    [Fact]
    public void PortRoutes_ContributeNothing() {
        // A port route carries no hostname and ck_routes_binding stores it Public; belt and braces.
        Assert.Null(AppApiTokens.ResolveAudience(
            Integrated(), [new AppApiTokens.RouteAudience(null, AccessMode.Public, null)]));
    }

    [Fact]
    public void NoIssuingEdge_ResolvesNothing() {
        // Auth off and no Cloudflare: nothing mints an assertion, whatever the routes say.
        Assert.Null(AppApiTokens.ResolveAudience(new WatchtowerOptions(), [Route("app.example.com")]));
        // No routes at all is the same answer arrived at from the other side.
        Assert.Null(AppApiTokens.ResolveAudience(Cloudflare(), []));
    }

    [Fact]
    public void CloudflareProviderSelected_ButDisabled_FallsThroughToIntegratedAuth() {
        // Mirrors the JWKS rule: a selected-but-disabled provider is not the edge doing the signing.
        var options = new WatchtowerOptions {
            PublicBaseUrl = "https://wt.example.com",
            Auth = new AuthOptions { Enabled = true },
            Proxy = new ProxyOptions { Enabled = false, Provider = "cloudflare" },
        };
        Assert.Equal("app.example.com", AppApiTokens.ResolveAudience(
            options, [Route("app.example.com", aud: "4714c1358e65fe6b")]));
    }

    [Fact]
    public void ThePlan_InjectsTheAudienceIntoEveryService_LikeTheJwksUrl() {
        var plan = EnvInjectionPlan.Create(new EnvInjectionRequest(
            [new EnvInjectionService("web"), new EnvInjectionService("worker")],
            StackId: 7,
            AppApiToken: "wtapp_x",
            AuthAudience: "4714c1358e65fe6b"));

        Assert.Equal(2, plan.Services.Count);
        foreach (var service in plan.Services)
            Assert.Contains(service.Variables, v =>
                v.Key == AppApiTokens.AudienceVariable && v.Value == "4714c1358e65fe6b");
    }

    [Fact]
    public void ThePlan_OmitsTheVariableEntirely_WhenNothingGatesTheStack() {
        var plan = EnvInjectionPlan.Create(new EnvInjectionRequest(
            [new EnvInjectionService("web")], StackId: 7, AppApiToken: "wtapp_x"));
        var service = Assert.Single(plan.Services);
        Assert.DoesNotContain(service.Variables, v => v.Key == AppApiTokens.AudienceVariable);
    }

    [Fact]
    public void TheVariable_IsReserved_SoOperatorValuesCannotShadowIt() =>
        Assert.Contains(AppApiTokens.AudienceVariable, AppApiTokens.Reserved);

    [Fact]
    public void ThePreview_NamesTheVariable_OnlyWhenTheStackHasAProtectedRoute() {
        var options = Integrated();
        Assert.Contains(
            Preview(options, [Route("app.example.com")]),
            v => v.Name == AppApiTokens.AudienceVariable && v.Value == "app.example.com");
        Assert.DoesNotContain(
            Preview(options, [Route("open.example.com", AccessMode.Public)]),
            v => v.Name == AppApiTokens.AudienceVariable);
    }

    [Fact]
    public void ThePreview_IsTheSameListADeployWrites_WithOnlyTheTokenMarkedSecret() {
        // One definition of "what a deploy writes", read by the deploy and by the API that previews
        // it. The token is the only credential in it; the rest name, locate or scope the stack.
        var preview = Preview(Integrated(), [Route("app.example.com")]);
        Assert.Equal(
            [
                AppApiTokens.TokenVariable, AppApiTokens.StackIdVariable, AppApiTokens.BaseUrlVariable,
                AppApiTokens.JwksUrlVariable, AppApiTokens.AudienceVariable,
            ],
            preview.Select(v => v.Name));
        Assert.Equal("7", Assert.Single(preview, v => v.Name == AppApiTokens.StackIdVariable).Value);
        var secret = Assert.Single(preview, v => v.Secret);
        Assert.Equal(AppApiTokens.TokenVariable, secret.Name);
        Assert.Equal("wtapp_x", secret.Value);
    }

    private static IReadOnlyList<AppApiTokens.InjectedVariable> Preview(
        WatchtowerOptions options, AppApiTokens.RouteAudience[] routes) =>
        AppApiTokens.InjectedVariables(options, stackId: 7, appApiToken: "wtapp_x", routes);
}
