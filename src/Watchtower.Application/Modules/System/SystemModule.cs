using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchtower.Application.Modules.System.Handlers;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.System;

/// <summary>Watchtower system management: self-update configuration, checks, apply, and docker config status.</summary>
[AppModule("System")]
public static partial class SystemModule {
    /// <summary>Returns the JSON type info resolver for System module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => SystemJsonContext.Default;
}

/// <summary>Status of the Docker CLI config file as seen from inside the Watchtower container.</summary>
public sealed record DockerConfigStatus(bool Exists, string Path, string Source);

/// <summary>JSON serializer context for System module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SelfUpdateStatus))]
[JsonSerializable(typeof(SelfUpdateConfig))]
[JsonSerializable(typeof(SelfUpdateRuntime))]
[JsonSerializable(typeof(DockerConfigStatus))]
[JsonSerializable(typeof(GetSelf.Query), TypeInfoPropertyName = "GetSelfQuery")]
[JsonSerializable(typeof(GetSelf.Response), TypeInfoPropertyName = "GetSelfResponse")]
[JsonSerializable(typeof(UpdateSelfConfiguration.Command), TypeInfoPropertyName = "UpdateSelfConfigurationCommand")]
[JsonSerializable(typeof(UpdateSelfConfiguration.Response), TypeInfoPropertyName = "UpdateSelfConfigurationResponse")]
[JsonSerializable(typeof(CheckSelfUpdate.Command), TypeInfoPropertyName = "CheckSelfUpdateCommand")]
[JsonSerializable(typeof(CheckSelfUpdate.Response), TypeInfoPropertyName = "CheckSelfUpdateResponse")]
[JsonSerializable(typeof(ApplySelfUpdate.Command), TypeInfoPropertyName = "ApplySelfUpdateCommand")]
[JsonSerializable(typeof(ApplySelfUpdate.Response), TypeInfoPropertyName = "ApplySelfUpdateResponse")]
[JsonSerializable(typeof(GetDockerConfig.Query), TypeInfoPropertyName = "GetDockerConfigQuery")]
[JsonSerializable(typeof(GetDockerConfig.Response), TypeInfoPropertyName = "GetDockerConfigResponse")]
[JsonSerializable(typeof(GetAutomation.Query), TypeInfoPropertyName = "GetAutomationQuery")]
[JsonSerializable(typeof(GetAutomation.Response), TypeInfoPropertyName = "GetAutomationResponse")]
[JsonSerializable(typeof(UpdateAutomation.Command), TypeInfoPropertyName = "UpdateAutomationCommand")]
[JsonSerializable(typeof(UpdateAutomation.Response), TypeInfoPropertyName = "UpdateAutomationResponse")]
public sealed partial class SystemJsonContext : JsonSerializerContext;
