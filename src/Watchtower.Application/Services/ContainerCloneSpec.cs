using System.Globalization;
using System.Text.Json.Nodes;

namespace Watchtower.Application.Services;

/// <summary>
/// Turns a raw container inspect record into the pieces needed to recreate that container on a new
/// image: the create-API body, the container name, and any additional networks to connect after
/// creation (the create endpoint accepts only one). Used by the self-update coordinator; pure JSON
/// transformation so the cloning rules stay unit-testable without a Docker daemon.
/// </summary>
public sealed record ContainerCloneSpec {
    /// <summary>Container name without Docker's leading slash.</summary>
    public required string Name { get; init; }

    /// <summary>Body for <c>POST /containers/create</c>: Config fields at the top level plus HostConfig/NetworkingConfig.</summary>
    public required JsonObject CreateBody { get; init; }

    /// <summary>Networks beyond the first, to connect after create and before start.</summary>
    public required IReadOnlyList<(string Network, JsonObject Endpoint)> ExtraNetworks { get; init; }

    // Per-endpoint fields the daemon assigns at connect time. Sending them back on create is at
    // best ignored and at worst pins stale addresses; user-specified static config (IPAMConfig,
    // Links, DriverOpts, Aliases) is what must survive.
    private static readonly string[] RuntimeEndpointFields = [
        "NetworkID", "EndpointID", "Gateway", "IPAddress", "IPPrefixLen",
        "IPv6Gateway", "GlobalIPv6Address", "GlobalIPv6PrefixLen", "MacAddress",
    ];

    /// <summary>
    /// Host ports to add to or remove from the clone (ADR-0033). Docker cannot add a port binding to a
    /// running container, so publishing one is a recreate — and a recreate is what this type already
    /// describes.
    /// </summary>
    /// <param name="Publish">
    /// Ports to publish. Host port equals container port: a port route's listener is inside the container
    /// on the same number an operator types in the browser, so any other mapping would be a second number
    /// nothing derives.
    /// </param>
    /// <param name="Unpublish">
    /// Ports to stop publishing — and only the mapping Watchtower would have made for them, host port
    /// equal to container port. Another host port mapped onto the same container port stays. A port named
    /// in both lists wins as a publish: the caller asking for both at once is asking for a state, and the
    /// state it named last is "bound".
    /// </param>
    public sealed record PortAmendments(IReadOnlyList<int> Publish, IReadOnlyList<int> Unpublish) {
        /// <summary>Nothing to change — the shape every non-port recreate passes.</summary>
        public static PortAmendments None { get; } = new([], []);

        public bool IsEmpty => Publish.Count == 0 && Unpublish.Count == 0;
    }

    /// <summary>
    /// Builds the clone spec from <paramref name="inspect"/> (a <c>GET /containers/{id}/json</c>
    /// response), retargeted to <paramref name="imageRef"/>.
    /// </summary>
    /// <param name="ports">
    /// Host-port bindings to add or remove on the way through, or null to clone them as they are. Only
    /// the ports it names are touched; every other binding the operator declared is carried over
    /// untouched, which is the whole safety property of this parameter.
    /// </param>
    public static ContainerCloneSpec FromInspect(
        JsonObject inspect, string imageRef, PortAmendments? ports = null) {
        var oldId = inspect["Id"]?.GetValue<string>() ?? "";
        var name = (inspect["Name"]?.GetValue<string>() ?? "").TrimStart('/');
        if (string.IsNullOrEmpty(name))
            throw new InvalidOperationException("Container inspect record has no name.");

        var body = inspect["Config"]?.DeepClone().AsObject()
            ?? throw new InvalidOperationException("Container inspect record has no Config block.");
        body["Image"] = imageRef;

        // Docker's default hostname is the container id prefix. Carrying it over would give the new
        // container the OLD container's id as hostname — and Watchtower finds itself via HOSTNAME,
        // so self-inspection would break. Drop it unless the user set an explicit custom hostname.
        if (body["Hostname"]?.GetValue<string>() is { Length: > 0 } hostname
            && oldId.StartsWith(hostname, StringComparison.Ordinal))
            body.Remove("Hostname");

        if (inspect["HostConfig"]?.DeepClone() is JsonNode hostConfig)
            body["HostConfig"] = hostConfig;

        if (ports is { IsEmpty: false }) ApplyPortAmendments(body, ports);

        var extraNetworks = new List<(string, JsonObject)>();
        if (inspect["NetworkSettings"]?["Networks"] is JsonObject networks && networks.Count > 0) {
            var oldShortId = oldId.Length >= 12 ? oldId[..12] : oldId;
            var sanitized = networks
                .Select(kv => (Network: kv.Key, Endpoint: SanitizeEndpoint(kv.Value?.AsObject(), oldShortId)))
                .ToList();

            // First network goes into the create body; the daemon rejects more than one there.
            body["NetworkingConfig"] = new JsonObject {
                ["EndpointsConfig"] = new JsonObject { [sanitized[0].Network] = sanitized[0].Endpoint },
            };
            extraNetworks.AddRange(sanitized.Skip(1).Select(s => (s.Network, s.Endpoint)));
        }

        return new ContainerCloneSpec { Name = name, CreateBody = body, ExtraNetworks = extraNetworks };
    }

    /// <summary>
    /// Adds and removes the named host ports on the create body, leaving every other binding alone.
    /// </summary>
    /// <remarks>
    /// Both halves of a published port are written: <c>ExposedPorts</c> (a Config field, so top level in
    /// the create body) and <c>HostConfig.PortBindings</c>. The daemon accepts a binding for a port that
    /// is not exposed, but <c>docker inspect</c> and every UI then disagree about what the container
    /// offers — and the next clone of this container would carry that disagreement forward.
    /// <para>
    /// A block that is missing, JSON null, or not an object at all is replaced with a fresh one. The
    /// first two are ordinary (a container with no published ports), and the third is not valid Docker
    /// input in the first place — nothing that could be salvaged is being thrown away.
    /// </para>
    /// <para>
    /// A removal is per <em>entry</em>, not per key: <c>PortBindings["9001/tcp"]</c> is an array, and an
    /// operator may have added a second mapping (<c>19001:9001</c>, or <c>127.0.0.1:9001:9001</c>) next to
    /// the one Watchtower published. Only the entry that <em>is</em> Watchtower's — that host port, on
    /// every interface — goes; the key, and its <c>ExposedPorts</c> twin, go with it only when nothing is
    /// left under them.
    /// </para>
    /// </remarks>
    private static void ApplyPortAmendments(JsonObject body, PortAmendments ports) {
        var hostConfig = Block(body, "HostConfig");
        var bindings = Block(hostConfig, "PortBindings");
        var exposed = Block(body, "ExposedPorts");

        // Removals first, and never for a port that is also being published: the caller named a state,
        // and "bound" is the one it named. Doing it in this order also makes the pair idempotent.
        //
        // A removal takes away the entry Watchtower published — host port equal to container port, on
        // every interface — and nothing else. One container port may carry several host mappings
        // ("19001:9001", or "127.0.0.1:9001:9001", alongside "9001:9001"), and dropping the whole array
        // would take away a mapping the operator declared, which is the one thing this file exists to
        // prevent. The key and its ExposedPorts twin go only when the last entry under them has.
        foreach (var port in ports.Unpublish.Where(p => !ports.Publish.Contains(p))) {
            var key = PortKey(port);
            if (bindings[key] is JsonArray entries) {
                for (var i = entries.Count - 1; i >= 0; i--)
                    if (IsHostPort(entries[i], port)) entries.RemoveAt(i);
                if (entries.Count > 0) continue;
            }
            bindings.Remove(key);
            exposed.Remove(key);
        }

        foreach (var port in ports.Publish) {
            var key = PortKey(port);
            // Replaced wholesale rather than merged: the binding Watchtower wants is the whole binding —
            // every interface, host port equal to container port — and a stale entry alongside it would
            // publish the same service somewhere nobody asked for.
            bindings[key] = new JsonArray(
                new JsonObject { ["HostPort"] = port.ToString(CultureInfo.InvariantCulture) });
            exposed[key] = new JsonObject();
        }
    }

    /// <summary>
    /// Whether a <c>PortBindings</c> entry is the mapping Watchtower publishes for
    /// <paramref name="port"/>: host port equal to container port, on every interface.
    /// </summary>
    /// <remarks>
    /// The interface is half the identity, not decoration. An operator who binds <c>127.0.0.1:9001:9001</c>
    /// next to Watchtower's own <c>9001:9001</c> has written a different mapping — same numbers, a
    /// deliberately narrower reach — and removing it on the strength of the host port alone would take
    /// away a binding Watchtower never made, which is the one thing this file exists to prevent. So the
    /// entry has to carry no <c>HostIp</c>, an empty one, or one of the two all-interfaces spellings,
    /// which is exactly the shape written above.
    /// <para>
    /// Docker writes the host port as a string; a number is accepted rather than treated as somebody
    /// else's entry. An empty host port ("any free port") never matches, because it is not a mapping this
    /// ever wrote. An array element that is not an object at all is not one either — a shape Docker would
    /// not have produced, and not a reason to throw in the middle of a recreate.
    /// </para>
    /// </remarks>
    private static bool IsHostPort(JsonNode? entry, int port) {
        if (entry is not JsonObject binding) return false;
        return IsAllInterfaces(binding["HostIp"]) && binding["HostPort"] switch {
            JsonValue v when v.TryGetValue<string>(out var text) =>
                int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                && parsed == port,
            JsonValue v when v.TryGetValue<int>(out var number) => number == port,
            _ => false,
        };
    }

    /// <summary>
    /// Whether a binding's <c>HostIp</c> means "every interface" — absent, JSON null, empty, or either of
    /// the two wildcard addresses the daemon writes for it.
    /// </summary>
    private static bool IsAllInterfaces(JsonNode? hostIp) {
        if (hostIp is not JsonValue value || !value.TryGetValue<string>(out var text)) return hostIp is null;
        return text.Length == 0 || text is "0.0.0.0" or "::";
    }

    /// <summary>Docker's key for a TCP port. UDP is out of scope: a port route serves HTTPS.</summary>
    private static string PortKey(int port) =>
        string.Create(CultureInfo.InvariantCulture, $"{port}/tcp");

    /// <summary>The named child object, created (or replaced, when it is not one) so it can be written to.</summary>
    private static JsonObject Block(JsonObject parent, string name) {
        if (parent[name] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static JsonObject SanitizeEndpoint(JsonObject? endpoint, string oldShortId) {
        var result = endpoint?.DeepClone().AsObject() ?? [];
        foreach (var field in RuntimeEndpointFields)
            result.Remove(field);

        // The old container's short id appears as an auto-added alias (and DNS name); the new
        // container gets its own, so advertising the stale one would resolve to nothing.
        RemoveFromArray(result, "Aliases", oldShortId);
        RemoveFromArray(result, "DNSNames", oldShortId);
        return result;
    }

    private static void RemoveFromArray(JsonObject endpoint, string key, string value) {
        if (endpoint[key] is not JsonArray array) return;
        for (var i = array.Count - 1; i >= 0; i--) {
            if (array[i]?.GetValue<string>() == value)
                array.RemoveAt(i);
        }
        if (array.Count == 0) endpoint.Remove(key);
    }
}
