using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Modules.Credentials;

/// <summary>
/// Manages general-purpose credentials (username + token pairs) used for git cloning and
/// Docker registry authentication. Handlers are auto-registered by the generated module defaults.
/// </summary>
[AppModule("Credentials")]
public static partial class CredentialsModule {
    /// <summary>Returns the JSON type info resolver for Credentials module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => CredentialsJsonContext.Default;
}

/// <summary>Public credential projection — the token is never included.</summary>
public sealed record CredentialDto(int Id, string Name, string Username, DateTimeOffset CreatedAt);
