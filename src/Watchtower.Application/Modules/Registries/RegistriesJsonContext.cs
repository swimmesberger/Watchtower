using System.Text.Json.Serialization;
using Watchtower.Application.Modules.Registries.Handlers;

namespace Watchtower.Application.Modules.Registries;

/// <summary>JSON serializer context for Registries module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RegistryDto))]
[JsonSerializable(typeof(ListRegistries.Query), TypeInfoPropertyName = "ListRegistriesQuery")]
[JsonSerializable(typeof(ListRegistries.Response), TypeInfoPropertyName = "ListRegistriesResponse")]
[JsonSerializable(typeof(CreateRegistry.Command), TypeInfoPropertyName = "CreateRegistryCommand")]
[JsonSerializable(typeof(CreateRegistry.Response), TypeInfoPropertyName = "CreateRegistryResponse")]
[JsonSerializable(typeof(UpdateRegistry.Command), TypeInfoPropertyName = "UpdateRegistryCommand")]
[JsonSerializable(typeof(UpdateRegistry.Response), TypeInfoPropertyName = "UpdateRegistryResponse")]
[JsonSerializable(typeof(DeleteRegistry.Command), TypeInfoPropertyName = "DeleteRegistryCommand")]
[JsonSerializable(typeof(DeleteRegistry.Response), TypeInfoPropertyName = "DeleteRegistryResponse")]
[JsonSerializable(typeof(TestRegistry.Command), TypeInfoPropertyName = "TestRegistryCommand")]
[JsonSerializable(typeof(TestRegistry.Response), TypeInfoPropertyName = "TestRegistryResponse")]
public sealed partial class RegistriesJsonContext : JsonSerializerContext;
