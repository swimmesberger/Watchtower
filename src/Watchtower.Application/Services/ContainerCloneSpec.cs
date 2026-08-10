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
    /// Builds the clone spec from <paramref name="inspect"/> (a <c>GET /containers/{id}/json</c>
    /// response), retargeted to <paramref name="imageRef"/>.
    /// </summary>
    public static ContainerCloneSpec FromInspect(JsonObject inspect, string imageRef) {
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
