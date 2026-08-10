using System.Text.Json.Nodes;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="ContainerCloneSpec.FromInspect"/> — the transformation the self-update
/// coordinator uses to recreate the Watchtower container on a new image.
/// </summary>
/// <remarks>
/// The cloning rules are the safety-critical part of an API-based self-update: a field that leaks
/// through wrongly (the old container's hostname, a stale alias) breaks self-detection or DNS after
/// the very first update, and a field that gets dropped (restart policy, binds) silently degrades
/// the deployment. Docker never exercises these rules in CI, so they are pinned here.
/// </remarks>
public sealed class ContainerCloneSpecTests {
    private const string OldId = "aabbccddeeff00112233445566778899aabbccddeeff00112233445566778899";

    private static JsonObject Inspect(Action<JsonObject>? mutate = null) {
        var inspect = new JsonObject {
            ["Id"] = OldId,
            ["Name"] = "/watchtower",
            ["Config"] = new JsonObject {
                ["Hostname"] = OldId[..12],
                ["Image"] = "ghcr.io/org/watchtower:latest",
                ["Env"] = new JsonArray("A=1", "B=2"),
                ["Labels"] = new JsonObject { ["com.docker.compose.project"] = "watchtower" },
            },
            ["HostConfig"] = new JsonObject {
                ["Binds"] = new JsonArray("/var/run/docker.sock:/var/run/docker.sock"),
                ["RestartPolicy"] = new JsonObject { ["Name"] = "unless-stopped" },
            },
            ["NetworkSettings"] = new JsonObject {
                ["Networks"] = new JsonObject {
                    ["watchtower_default"] = new JsonObject {
                        ["Aliases"] = new JsonArray("watchtower", OldId[..12]),
                        ["DNSNames"] = new JsonArray("watchtower", OldId[..12]),
                        ["IPAMConfig"] = null,
                        ["NetworkID"] = "netid",
                        ["EndpointID"] = "epid",
                        ["Gateway"] = "172.18.0.1",
                        ["IPAddress"] = "172.18.0.2",
                        ["IPPrefixLen"] = 16,
                        ["MacAddress"] = "02:42:ac:12:00:02",
                    },
                },
            },
        };
        mutate?.Invoke(inspect);
        return inspect;
    }

    [Fact]
    public void RetargetsTheImageAndKeepsConfigAndHostConfig() {
        var spec = ContainerCloneSpec.FromInspect(Inspect(), "ghcr.io/org/watchtower:v2");

        Assert.Equal("watchtower", spec.Name);
        Assert.Equal("ghcr.io/org/watchtower:v2", spec.CreateBody["Image"]!.GetValue<string>());
        Assert.Equal("A=1", spec.CreateBody["Env"]![0]!.GetValue<string>());
        Assert.Equal("unless-stopped",
            spec.CreateBody["HostConfig"]!["RestartPolicy"]!["Name"]!.GetValue<string>());
        Assert.Equal("/var/run/docker.sock:/var/run/docker.sock",
            spec.CreateBody["HostConfig"]!["Binds"]![0]!.GetValue<string>());
    }

    [Fact]
    public void DropsTheDefaultHostnameSoTheNewContainerGetsItsOwn() {
        // Watchtower locates itself via HOSTNAME; inheriting the old container's id-derived
        // hostname would point self-inspection at a container that no longer exists.
        var spec = ContainerCloneSpec.FromInspect(Inspect(), "img:v2");

        Assert.False(spec.CreateBody.ContainsKey("Hostname"));
    }

    [Fact]
    public void KeepsAnExplicitCustomHostname() {
        var inspect = Inspect(i => i["Config"]!["Hostname"] = "my-watchtower");

        var spec = ContainerCloneSpec.FromInspect(inspect, "img:v2");

        Assert.Equal("my-watchtower", spec.CreateBody["Hostname"]!.GetValue<string>());
    }

    [Fact]
    public void SanitizesTheEndpointButKeepsUserFacingSettings() {
        var spec = ContainerCloneSpec.FromInspect(Inspect(), "img:v2");

        var endpoint = spec.CreateBody["NetworkingConfig"]!["EndpointsConfig"]!["watchtower_default"]!.AsObject();
        // Runtime-assigned fields must not be pinned onto the new container.
        Assert.False(endpoint.ContainsKey("IPAddress"));
        Assert.False(endpoint.ContainsKey("EndpointID"));
        Assert.False(endpoint.ContainsKey("MacAddress"));
        // The service alias survives; the old container's short-id alias does not.
        Assert.Equal(["watchtower"], endpoint["Aliases"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal(["watchtower"], endpoint["DNSNames"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void SplitsAdditionalNetworksForPostCreateConnect() {
        // The create endpoint accepts a single network; the rest must be connected afterwards.
        var inspect = Inspect(i => {
            var networks = i["NetworkSettings"]!["Networks"]!.AsObject();
            networks["second_net"] = new JsonObject { ["Aliases"] = new JsonArray("wt-alt") };
        });

        var spec = ContainerCloneSpec.FromInspect(inspect, "img:v2");

        Assert.Single(spec.CreateBody["NetworkingConfig"]!["EndpointsConfig"]!.AsObject());
        var (network, endpoint) = Assert.Single(spec.ExtraNetworks);
        Assert.Equal("second_net", network);
        Assert.Equal("wt-alt", endpoint["Aliases"]![0]!.GetValue<string>());
    }

    [Fact]
    public void OmitsNetworkingConfigWhenTheContainerHasNoNetworks() {
        var inspect = Inspect(i => i["NetworkSettings"]!["Networks"] = new JsonObject());

        var spec = ContainerCloneSpec.FromInspect(inspect, "img:v2");

        Assert.False(spec.CreateBody.ContainsKey("NetworkingConfig"));
        Assert.Empty(spec.ExtraNetworks);
    }
}
