using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Golden tests for the generated Caddyfile. The output is a contract with two audiences that cannot be
/// reasoned about from C#: Caddy itself, and the operator reading the file to work out what their proxy
/// is doing. Comparing whole documents rather than asserting on fragments is what makes an accidental
/// change to either one visible.
/// </summary>
public sealed class CaddyConfigBuilderTests {
    private static readonly CaddyGlobals Globals =
        new("ops@example.invalid", AdminPort: 2019, AskUrl: "http://watchtower:8080/api/proxy/ask");

    [Fact]
    public void PublicSites_RenderExactlyAsTheyAlwaysHave() {
        // Access control is opt-in per route, and a deployment that never opts in must get byte-for-byte
        // what it got before any of this existed — operators and downstream tooling read this file.
        var sites = new[] {
            new CaddySite("public.example.invalid", "demo-web", 8080, Tls: true),
            new CaddySite("plain.example.invalid", "demo-web", 3000, Tls: false),
            new CaddySite("custom.example.invalid", "demo-web", 8080, Tls: true, OnDemand: true),
        };

        Assert.Equal("""
            {
            	admin 0.0.0.0:2019
            	email ops@example.invalid
            	on_demand_tls {
            		ask http://watchtower:8080/api/proxy/ask
            	}
            }

            custom.example.invalid {
            	tls {
            		on_demand
            	}
            	reverse_proxy demo-web:8080
            }

            http://plain.example.invalid {
            	reverse_proxy demo-web:3000
            }

            public.example.invalid {
            	reverse_proxy demo-web:8080
            }

            """.ReplaceLineEndings("\n"),
            CaddyConfigBuilder.Build(sites, Globals));
    }

    [Fact]
    public void MixedEstate_RendersTheProtectedShapeFromTheDesign() {
        // One of each: an app that does its own auth, one behind "any signed-in user", one behind an
        // explicit grant list, and Watchtower's own self-route (never protected — see CaddyManager).
        var sites = new[] {
            new CaddySite("public.example.invalid", "demo-web", 8080, Tls: true),
            new CaddySite("members.example.invalid", "demo-web", 3000, Tls: true, Protected: true),
            new CaddySite("secret.example.invalid", "other-api", 9000, Tls: true, OnDemand: true, Protected: true),
            new CaddySite("watchtower.example.invalid", "watchtower", 8080, Tls: true),
        };

        Assert.Equal("""
            {
            	admin 0.0.0.0:2019
            	email ops@example.invalid
            	on_demand_tls {
            		ask http://watchtower:8080/api/proxy/ask
            	}
            }

            members.example.invalid {
            	handle /.watchtower/* {
            		reverse_proxy watchtower:8080
            	}
            	handle {
            		request_header -X-Watchtower-User
            		request_header -X-Watchtower-Email
            		request_header -X-Watchtower-Jwt
            		forward_auth watchtower:8080 {
            			uri /api/access/verify
            			copy_headers X-Watchtower-User X-Watchtower-Email X-Watchtower-Jwt
            		}
            		reverse_proxy demo-web:3000
            	}
            }

            public.example.invalid {
            	reverse_proxy demo-web:8080
            }

            secret.example.invalid {
            	tls {
            		on_demand
            	}
            	handle /.watchtower/* {
            		reverse_proxy watchtower:8080
            	}
            	handle {
            		request_header -X-Watchtower-User
            		request_header -X-Watchtower-Email
            		request_header -X-Watchtower-Jwt
            		forward_auth watchtower:8080 {
            			uri /api/access/verify
            			copy_headers X-Watchtower-User X-Watchtower-Email X-Watchtower-Jwt
            		}
            		reverse_proxy other-api:9000
            	}
            }

            watchtower.example.invalid {
            	reverse_proxy watchtower:8080
            }

            """.ReplaceLineEndings("\n"),
            CaddyConfigBuilder.Build(sites, Globals));
    }

    [Fact]
    public void HeaderStripping_PrecedesForwardAuth_ForEveryIdentityHeader() {
        var caddyfile = CaddyConfigBuilder.Build(
            [new CaddySite("app.example.invalid", "demo-web", 8080, Tls: true, Protected: true)], Globals);

        var forwardAuthAt = caddyfile.IndexOf("forward_auth", StringComparison.Ordinal);
        Assert.True(forwardAuthAt > 0);

        foreach (var header in RouteAccessPolicy.IdentityHeaderNames) {
            var stripAt = caddyfile.IndexOf($"request_header -{header}\n", StringComparison.Ordinal);
            // Order is the whole control: a header stripped *after* forward_auth ran would erase the
            // verified value, and one never stripped would let the client assert it (design.md §2.3).
            Assert.True(stripAt > 0, $"{header} is never stripped from the inbound request.");
            Assert.True(stripAt < forwardAuthAt, $"{header} is stripped after forward_auth, not before.");
            Assert.Contains($" {header}", caddyfile.AsSpan()[forwardAuthAt..].ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReservedPrefix_IsHandledBeforeTheAuthenticatedBlock() {
        var caddyfile = CaddyConfigBuilder.Build(
            [new CaddySite("app.example.invalid", "demo-web", 8080, Tls: true, Protected: true)], Globals);

        // The callback that mints the app session runs while the visitor is still anonymous, so its handler
        // has to be matched before the one that would send them back to the login page.
        Assert.True(
            caddyfile.IndexOf("handle /.watchtower/*", StringComparison.Ordinal) <
            caddyfile.IndexOf("forward_auth", StringComparison.Ordinal));
    }
}
