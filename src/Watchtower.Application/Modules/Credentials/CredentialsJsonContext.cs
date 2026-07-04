using System.Text.Json.Serialization;
using Watchtower.Application.Modules.Credentials.Handlers;

namespace Watchtower.Application.Modules.Credentials;

/// <summary>JSON serializer context for Credentials module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(CredentialDto))]
[JsonSerializable(typeof(ListCredentials.Query), TypeInfoPropertyName = "ListCredentialsQuery")]
[JsonSerializable(typeof(ListCredentials.Response), TypeInfoPropertyName = "ListCredentialsResponse")]
[JsonSerializable(typeof(CreateCredential.Command), TypeInfoPropertyName = "CreateCredentialCommand")]
[JsonSerializable(typeof(CreateCredential.Response), TypeInfoPropertyName = "CreateCredentialResponse")]
[JsonSerializable(typeof(UpdateCredential.Command), TypeInfoPropertyName = "UpdateCredentialCommand")]
[JsonSerializable(typeof(UpdateCredential.Response), TypeInfoPropertyName = "UpdateCredentialResponse")]
[JsonSerializable(typeof(DeleteCredential.Command), TypeInfoPropertyName = "DeleteCredentialCommand")]
[JsonSerializable(typeof(DeleteCredential.Response), TypeInfoPropertyName = "DeleteCredentialResponse")]
public sealed partial class CredentialsJsonContext : JsonSerializerContext;
