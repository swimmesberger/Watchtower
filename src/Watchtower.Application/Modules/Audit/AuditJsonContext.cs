using System.Text.Json.Serialization;
using Watchtower.Application.Modules.Audit.Handlers;

namespace Watchtower.Application.Modules.Audit;

/// <summary>JSON serializer context for Audit module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AuditEventDto))]
[JsonSerializable(typeof(ListAuditEvents.Query), TypeInfoPropertyName = "ListAuditEventsQuery")]
[JsonSerializable(typeof(ListAuditEvents.Response), TypeInfoPropertyName = "ListAuditEventsResponse")]
[JsonSerializable(typeof(ListAuditFacets.Query), TypeInfoPropertyName = "ListAuditFacetsQuery")]
[JsonSerializable(typeof(ListAuditFacets.Response), TypeInfoPropertyName = "ListAuditFacetsResponse")]
public sealed partial class AuditJsonContext : JsonSerializerContext;
