using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the JWKS injection that makes the edge swap zero-config for apps: deploys inject
/// <c>WATCHTOWER_AUTH_JWKS_URL</c> resolved from the active edge, so an app verifies its identity
/// assertion (<c>Cf-Access-Jwt-Assertion</c> / <c>X-Watchtower-Jwt</c>) against whatever is actually
/// signing it — no hard-coded issuer to change when switching between Cloudflare Access and
/// integrated auth.
/// </summary>
public sealed class AuthJwksInjectionTests {
    [Fact]
    public void CloudflareProvider_ResolvesTheTeamCertsUrl() {
        var options = new WatchtowerOptions {
            Proxy = new ProxyOptions {
                Enabled = true,
                Provider = "cloudflare",
                Cloudflare = new CloudflareProxyOptions { TeamDomain = "myteam" },
            },
        };
        Assert.Equal("https://myteam.cloudflareaccess.com/cdn-cgi/access/certs", AppApiTokens.ResolveJwksUrl(options));
    }

    [Fact]
    public void TeamDomain_AcceptsTheFullHostToo() {
        var options = new WatchtowerOptions {
            Proxy = new ProxyOptions {
                Enabled = true,
                Provider = "cloudflare",
                Cloudflare = new CloudflareProxyOptions { TeamDomain = "myteam.cloudflareaccess.com" },
            },
        };
        Assert.Equal("https://myteam.cloudflareaccess.com/cdn-cgi/access/certs", AppApiTokens.ResolveJwksUrl(options));
    }

    [Fact]
    public void IntegratedAuth_ResolvesWatchtowersOwnJwks() {
        var options = new WatchtowerOptions {
            PublicBaseUrl = "https://watchtower.example.com/",
            Auth = new AuthOptions { Enabled = true },
        };
        Assert.Equal("https://watchtower.example.com/api/auth/jwks", AppApiTokens.ResolveJwksUrl(options));
    }

    [Fact]
    public void NoIssuingEdge_ResolvesNothing() {
        // Auth off, proxy off — nothing signs assertions, so nothing is injected.
        Assert.Null(AppApiTokens.ResolveJwksUrl(new WatchtowerOptions()));
        // Cloudflare active but no team configured: the URL cannot be derived, so stay silent rather
        // than inject something wrong.
        Assert.Null(AppApiTokens.ResolveJwksUrl(new WatchtowerOptions {
            Proxy = new ProxyOptions { Enabled = true, Provider = "cloudflare" },
        }));
        // Integrated auth without a public base URL: the JWKS has no reachable address.
        Assert.Null(AppApiTokens.ResolveJwksUrl(new WatchtowerOptions {
            Auth = new AuthOptions { Enabled = true },
        }));
    }

    [Fact]
    public void CloudflareProviderSelected_ButDisabled_FallsThroughToIntegratedAuth() {
        var options = new WatchtowerOptions {
            PublicBaseUrl = "https://wt.example.com",
            Auth = new AuthOptions { Enabled = true },
            Proxy = new ProxyOptions {
                Enabled = false,
                Provider = "cloudflare",
                Cloudflare = new CloudflareProxyOptions { TeamDomain = "myteam" },
            },
        };
        Assert.Equal("https://wt.example.com/api/auth/jwks", AppApiTokens.ResolveJwksUrl(options));
    }

    [Fact]
    public void ThePlan_InjectsTheJwksUrlIntoEveryService_LikeTheBaseUrl() {
        var plan = EnvInjectionPlan.Create(new EnvInjectionRequest(
            [new EnvInjectionService("web"), new EnvInjectionService("worker")],
            StackId: 7,
            AppApiToken: "wtapp_x",
            PublicBaseUrl: "https://wt.example.com",
            TargetServiceName: null,
            AuthJwksUrl: "https://myteam.cloudflareaccess.com/cdn-cgi/access/certs"));

        Assert.Equal(2, plan.Services.Count);
        foreach (var service in plan.Services)
            Assert.Contains(service.Variables, v =>
                v.Key == AppApiTokens.JwksUrlVariable
                && v.Value == "https://myteam.cloudflareaccess.com/cdn-cgi/access/certs");
    }

    [Fact]
    public void ThePlan_OmitsTheVariableEntirely_WhenNoEdgeIssues() {
        var plan = EnvInjectionPlan.Create(new EnvInjectionRequest(
            [new EnvInjectionService("web")], StackId: 7, AppApiToken: "wtapp_x"));
        var service = Assert.Single(plan.Services);
        Assert.DoesNotContain(service.Variables, v => v.Key == AppApiTokens.JwksUrlVariable);
    }

    [Fact]
    public void TheVariable_IsReserved_SoOperatorValuesCannotShadowIt() =>
        Assert.Contains(AppApiTokens.JwksUrlVariable, AppApiTokens.Reserved);
}
