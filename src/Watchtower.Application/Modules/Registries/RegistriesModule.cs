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

/// <summary>
/// A read-only registry entry found in the host docker config (<c>WATCHTOWER_DOCKER_CONFIG</c> /
/// <c>DOCKER_CONFIG</c> / <c>~/.docker</c>). Managed by <c>docker login</c> on the host, not by
/// Watchtower; usable for pulls and as a CI sync source, never editable here. Username is null for
/// credential-helper entries (their secrets are not readable from the config file).
/// </summary>
public sealed record HostRegistryDto(string Url, string? Username);
