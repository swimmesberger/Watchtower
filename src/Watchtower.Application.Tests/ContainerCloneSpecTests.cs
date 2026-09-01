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

    // ── Port amendments (ADR-0033) ───────────────────────────────────────────

    /// <summary>
    /// The clone is also how a port route's host port gets published, since Docker cannot add a binding
    /// to a running container. Both halves have to be written: the binding is what actually publishes,
    /// and the exposed entry is what every inspect and UI reads back — including the next clone of this
    /// container, which would otherwise carry the disagreement forward.
    /// </summary>
    [Fact]
    public void PublishingAPort_WritesTheBindingAndTheExposedEntry() {
        var spec = ContainerCloneSpec.FromInspect(Inspect(), "img:v2", Publish(9001));

        Assert.Equal("9001", HostPort(spec, 9001));
        Assert.True(Exposed(spec).ContainsKey("9001/tcp"));
    }

    /// <summary>Host port equals container port: the operator types the number the listener uses.</summary>
    [Fact]
    public void PublishingSeveralPorts_MapsEachOntoItself() {
        var spec = ContainerCloneSpec.FromInspect(Inspect(), "img:v2", Publish(9001, 9002));

        Assert.Equal("9001", HostPort(spec, 9001));
        Assert.Equal("9002", HostPort(spec, 9002));
    }

    /// <summary>
    /// The safety property of the whole feature: a binding the operator declared is not this method's to
    /// touch, and a port never named is never looked at.
    /// </summary>
    [Fact]
    public void PublishingAPort_LeavesTheOperatorsOwnBindingsAlone() {
        var inspect = Inspect(i => i["HostConfig"]!["PortBindings"] = new JsonObject {
            ["8080/tcp"] = new JsonArray(new JsonObject { ["HostIp"] = "127.0.0.1", ["HostPort"] = "18080" }),
        });

        var spec = ContainerCloneSpec.FromInspect(inspect, "img:v2", Publish(9001));

        var bindings = Bindings(spec);
        Assert.Equal("18080", bindings["8080/tcp"]![0]!["HostPort"]!.GetValue<string>());
        // Down to the interface it was pinned to — the entry is carried across, not rebuilt.
        Assert.Equal("127.0.0.1", bindings["8080/tcp"]![0]!["HostIp"]!.GetValue<string>());
        Assert.Equal("9001", bindings["9001/tcp"]![0]!["HostPort"]!.GetValue<string>());
    }

    [Fact]
    public void UnpublishingAPort_RemovesBothHalvesAndNothingElse() {
        var inspect = WithPublished(9001, 9002);

        var spec = ContainerCloneSpec.FromInspect(
            inspect, "img:v2", new ContainerCloneSpec.PortAmendments([], [9001]));

        Assert.False(Bindings(spec).ContainsKey("9001/tcp"));
        Assert.False(Exposed(spec).ContainsKey("9001/tcp"));
        Assert.Equal("9002", HostPort(spec, 9002));
    }

    [Fact]
    public void PublishingAndUnpublishingDisjointPorts_DoesBoth() {
        var inspect = WithPublished(9003);

        var spec = ContainerCloneSpec.FromInspect(
            inspect, "img:v2", new ContainerCloneSpec.PortAmendments([9001], [9003]));

        Assert.Equal("9001", HostPort(spec, 9001));
        Assert.False(Bindings(spec).ContainsKey("9003/tcp"));
    }

    /// <summary>
    /// A port on both lists is a caller naming a state rather than two operations; "bound" is the state
    /// it named last, and saying so here means the caller never has to order the lists.
    /// </summary>
    [Fact]
    public void APortOnBothLists_IsPublished() {
        var spec = ContainerCloneSpec.FromInspect(
            Inspect(), "img:v2", new ContainerCloneSpec.PortAmendments([9001], [9001]));

        Assert.Equal("9001", HostPort(spec, 9001));
        Assert.True(Exposed(spec).ContainsKey("9001/tcp"));
    }

    /// <summary>
    /// Applying the same publish to a container that already has it changes nothing — which is what lets
    /// a plan be recomputed and reapplied without a growing pile of duplicate bindings.
    /// </summary>
    [Fact]
    public void RepublishingAPortThatIsAlreadyBound_IsIdempotent() {
        var spec = ContainerCloneSpec.FromInspect(WithPublished(9001), "img:v2", Publish(9001));

        var entries = Bindings(spec)["9001/tcp"]!.AsArray();
        Assert.Single(entries);
        Assert.Equal("9001", entries[0]!["HostPort"]!.GetValue<string>());
    }

    /// <summary>
    /// A container that publishes nothing has neither block, and older daemons write them as JSON null
    /// rather than omitting them. Both are the ordinary shape of "no ports", not a reason to fail the
    /// recreate this process is in the middle of.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PublishingOntoAContainerWithNoPortBlocks_CreatesThem(bool explicitNulls) {
        var inspect = Inspect(i => {
            if (!explicitNulls) return;
            i["Config"]!["ExposedPorts"] = null;
            i["HostConfig"]!["PortBindings"] = null;
        });

        var spec = ContainerCloneSpec.FromInspect(inspect, "img:v2", Publish(9001));

        Assert.Equal("9001", HostPort(spec, 9001));
        Assert.True(Exposed(spec).ContainsKey("9001/tcp"));
    }

    /// <summary>Not even a HostConfig block: the amendment brings one rather than dropping the ports.</summary>
    [Fact]
    public void PublishingOntoAnInspectWithNoHostConfig_StillBinds() {
        var inspect = Inspect(i => i.Remove("HostConfig"));

        var spec = ContainerCloneSpec.FromInspect(inspect, "img:v2", Publish(9001));

        Assert.Equal("9001", HostPort(spec, 9001));
    }

    /// <summary>
    /// A block that is not an object is not something Docker would have accepted either, so it is
    /// replaced rather than parsed. What matters is that the recreate goes ahead: this runs between the
    /// stop and the create, where throwing leaves the container down.
    /// </summary>
    [Fact]
    public void PublishingOntoAMalformedPortBlock_ReplacesIt() {
        var inspect = Inspect(i => i["HostConfig"]!["PortBindings"] = "not an object");

        var spec = ContainerCloneSpec.FromInspect(inspect, "img:v2", Publish(9001));

        Assert.Equal("9001", HostPort(spec, 9001));
    }

    /// <summary>An amendment with nothing in it is not an amendment: the body comes through untouched.</summary>
    [Fact]
    public void AnEmptyAmendment_AddsNoPortBlocks() {
        var spec = ContainerCloneSpec.FromInspect(Inspect(), "img:v2", ContainerCloneSpec.PortAmendments.None);

        Assert.False(spec.CreateBody.ContainsKey("ExposedPorts"));
        Assert.False(spec.CreateBody["HostConfig"]!.AsObject().ContainsKey("PortBindings"));
    }

    /// <summary>Unpublishing a port nothing binds is a no-op, not a failure.</summary>
    [Fact]
    public void UnpublishingAPortThatIsNotBound_ChangesNothing() {
        var spec = ContainerCloneSpec.FromInspect(
            WithPublished(9002), "img:v2", new ContainerCloneSpec.PortAmendments([], [9001]));

        Assert.Equal("9002", HostPort(spec, 9002));
        Assert.Single(Bindings(spec));
    }

    private static ContainerCloneSpec.PortAmendments Publish(params int[] ports) => new(ports, []);

    /// <summary>An inspect record whose container already publishes <paramref name="ports"/>.</summary>
    private static JsonObject WithPublished(params int[] ports) => Inspect(i => {
        var bindings = new JsonObject();
        var exposed = new JsonObject();
        foreach (var port in ports) {
            bindings[$"{port}/tcp"] = new JsonArray(new JsonObject { ["HostPort"] = port.ToString() });
            exposed[$"{port}/tcp"] = new JsonObject();
        }
        i["HostConfig"]!["PortBindings"] = bindings;
        i["Config"]!["ExposedPorts"] = exposed;
    });

    private static JsonObject Bindings(ContainerCloneSpec spec) =>
        spec.CreateBody["HostConfig"]!["PortBindings"]!.AsObject();

    private static JsonObject Exposed(ContainerCloneSpec spec) => spec.CreateBody["ExposedPorts"]!.AsObject();

    private static string HostPort(ContainerCloneSpec spec, int port) =>
        Bindings(spec)[$"{port}/tcp"]![0]!["HostPort"]!.GetValue<string>();
}
