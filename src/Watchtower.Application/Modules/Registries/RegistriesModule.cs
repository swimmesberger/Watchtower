using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Modules.Registries;

/// <summary>Manages Docker registry entries linked to stored credentials.</summary>
[AppModule("Registries")]
public static partial class RegistriesModule {
    /// <summary>Returns the JSON type info resolver for Registries module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => RegistriesJsonContext.Default;
}

/// <summary>Public registry projection — includes the linked credential name for display.</summary>
public sealed record RegistryDto(
    int Id, string Name, string Url, int? CredentialId, string? CredentialName, DateTimeOffset CreatedAt);
