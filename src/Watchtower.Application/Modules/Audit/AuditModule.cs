using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Watchtower.Application.Modules.Audit.Handlers;

namespace Watchtower.Application.Modules.Audit;

/// <summary>
/// Watchtower's general audit trail: what Watchtower changed, where, and whether it worked —
/// chiefly writes against external control planes. The first populated category is
/// <c>proxy.cloudflare</c>; future planes (deploys, settings, CI) record into the same table and
/// are read through the same <c>audit.*</c> surface.
/// </summary>
[AppModule("Audit")]
public static partial class AuditModule {
    /// <summary>Returns the JSON type info resolver for Audit module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => AuditJsonContext.Default;
}

/// <summary>JSON serializer context for Audit module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AuditEventDto))]
[JsonSerializable(typeof(ListAuditEvents.Query), TypeInfoPropertyName = "ListAuditEventsQuery")]
[JsonSerializable(typeof(ListAuditEvents.Response), TypeInfoPropertyName = "ListAuditEventsResponse")]
public sealed partial class AuditJsonContext : JsonSerializerContext;
