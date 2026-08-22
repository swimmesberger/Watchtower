using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watchtower.Application.Services.Acme;

/// <summary>Source-generated serializers for the ACME wire types.</summary>
/// <remarks>
/// Camel-case and null-omitting to match RFC 8555's own JSON, which the CAs are strict about: a
/// <c>"contact": null</c> is not the same request as one with no contact field, and at least one CA
/// rejects the former.
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AcmeDirectory))]
[JsonSerializable(typeof(AcmeDirectoryMeta))]
[JsonSerializable(typeof(AcmeAccount))]
[JsonSerializable(typeof(AcmeIdentifier))]
[JsonSerializable(typeof(AcmeOrder))]
[JsonSerializable(typeof(AcmeAuthorization))]
[JsonSerializable(typeof(AcmeChallenge))]
[JsonSerializable(typeof(AcmeProblem))]
[JsonSerializable(typeof(AcmeSubproblem))]
[JsonSerializable(typeof(NewAccountPayload))]
[JsonSerializable(typeof(NewOrderPayload))]
[JsonSerializable(typeof(FinalizePayload))]
[JsonSerializable(typeof(AcmeAccountFile))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class AcmeJsonContext : JsonSerializerContext;
