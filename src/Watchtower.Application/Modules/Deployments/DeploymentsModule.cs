using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchtower.Application.Modules.Deployments.Handlers;

namespace Watchtower.Application.Modules.Deployments;

/// <summary>Cross-stack deployment visibility (the dashboard live feed).</summary>
[AppModule("Deployments")]
public static partial class DeploymentsModule {
    /// <summary>Returns the JSON type info resolver for Deployments module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => DeploymentsJsonContext.Default;
}

/// <summary>An in-progress (queued or running) deploy event enriched with the stack name.</summary>
public sealed record ActiveDeploymentDto(
    int Id, int StackId, string StackName, string Status, string TriggeredBy, DateTimeOffset StartedAt);

/// <summary>JSON serializer context for Deployments module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ActiveDeploymentDto))]
[JsonSerializable(typeof(ListActiveDeployments.Query), TypeInfoPropertyName = "ListActiveDeploymentsQuery")]
[JsonSerializable(typeof(ListActiveDeployments.Response), TypeInfoPropertyName = "ListActiveDeploymentsResponse")]
public sealed partial class DeploymentsJsonContext : JsonSerializerContext;
