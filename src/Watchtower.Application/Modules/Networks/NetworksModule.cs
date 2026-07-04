using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchtower.Application.Modules.Networks.Handlers;

namespace Watchtower.Application.Modules.Networks;

/// <summary>Docker network inspection and the published-port exposure map (list, ports).</summary>
[AppModule("Networks")]
public static partial class NetworksModule {
    /// <summary>Returns the JSON type info resolver for Networks module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => NetworksJsonContext.Default;
}

/// <summary>IPAM addressing for a network (first config entry's subnet + gateway).</summary>
public sealed record NetworkIpamDto(string? Subnet, string? Gateway);

/// <summary>A container attached to a network, with its resolved stack name and addresses.</summary>
public sealed record NetworkEndpointDto(
    string ContainerId,
    string ContainerName,
    string? StackName,
    string? Ipv4,
    string? Ipv6);

/// <summary>
/// A Docker network enriched with compose context, attachment, and a server-computed lifecycle.
/// </summary>
/// <param name="Lifecycle">
/// <c>live</c> = ≥1 attached container; <c>declared</c> = has a compose project label but no
/// attachment; <c>orphaned</c> = no project label AND no attachment (excludes docker defaults, which
/// are always <c>live</c>-treated via <see cref="IsDefault"/>). NOTE: attachment here is the inspect
/// endpoint's container map, which reflects RUNNING containers only — Docker does not attach stopped
/// containers to a network — so <c>refCount</c> is the running-attachment count.
/// </param>
public sealed record NetworkDto(
    string Id,
    string Name,
    string Driver,
    string Scope,
    bool Internal,
    string? Project,
    string? ComposeNetwork,
    string? CreatedAt,
    IReadOnlyDictionary<string, string> Labels,
    NetworkIpamDto Ipam,
    IReadOnlyList<NetworkEndpointDto> Attached,
    int RefCount,
    string Lifecycle,
    bool IsDefault);

/// <summary>A single published (or exposed-but-unpublished) container port with derived exposure.</summary>
public sealed record PublishedPortDto(
    string ContainerId,
    string ContainerName,
    string? StackName,
    int PrivatePort,
    int? PublicPort,
    string Protocol,
    string HostIp,
    string Exposure);

/// <summary>≥2 containers claiming the same (hostIp, publicPort, protocol) tuple.</summary>
public sealed record PortConflictDto(
    int PublicPort,
    string Protocol,
    string HostIp,
    IReadOnlyList<string> ContainerNames);

/// <summary>JSON serializer context for Networks module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(NetworkDto))]
[JsonSerializable(typeof(NetworkEndpointDto))]
[JsonSerializable(typeof(NetworkIpamDto))]
[JsonSerializable(typeof(PublishedPortDto))]
[JsonSerializable(typeof(PortConflictDto))]
[JsonSerializable(typeof(ListNetworks.Query), TypeInfoPropertyName = "ListNetworksQuery")]
[JsonSerializable(typeof(ListNetworks.Response), TypeInfoPropertyName = "ListNetworksResponse")]
[JsonSerializable(typeof(ListPublishedPorts.Query), TypeInfoPropertyName = "ListPublishedPortsQuery")]
[JsonSerializable(typeof(ListPublishedPorts.Response), TypeInfoPropertyName = "ListPublishedPortsResponse")]
public sealed partial class NetworksJsonContext : JsonSerializerContext;
