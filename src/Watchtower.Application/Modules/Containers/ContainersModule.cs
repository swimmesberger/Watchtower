using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchtower.Application.Modules.Containers.Handlers;

namespace Watchtower.Application.Modules.Containers;

/// <summary>Docker container inspection and lifecycle management (list, restart, stop, remove).</summary>
[AppModule("Containers")]
public static partial class ContainersModule {
    /// <summary>Returns the JSON type info resolver for Containers module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => ContainersJsonContext.Default;
}

/// <summary>Container summary enriched with the resolved stack name and parsed health state.</summary>
public sealed record ContainerDto(
    string Id, string[] Names, string Image, string State, string Status, string? Health, string? StackName);

/// <summary>JSON serializer context for Containers module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ContainerDto))]
[JsonSerializable(typeof(ListContainers.Query), TypeInfoPropertyName = "ListContainersQuery")]
[JsonSerializable(typeof(ListContainers.Response), TypeInfoPropertyName = "ListContainersResponse")]
[JsonSerializable(typeof(RestartContainer.Command), TypeInfoPropertyName = "RestartContainerCommand")]
[JsonSerializable(typeof(RestartContainer.Response), TypeInfoPropertyName = "RestartContainerResponse")]
[JsonSerializable(typeof(StopContainer.Command), TypeInfoPropertyName = "StopContainerCommand")]
[JsonSerializable(typeof(StopContainer.Response), TypeInfoPropertyName = "StopContainerResponse")]
[JsonSerializable(typeof(RemoveContainer.Command), TypeInfoPropertyName = "RemoveContainerCommand")]
[JsonSerializable(typeof(RemoveContainer.Response), TypeInfoPropertyName = "RemoveContainerResponse")]
public sealed partial class ContainersJsonContext : JsonSerializerContext;
