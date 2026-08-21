using System.Text.Json.Serialization;
using Watchtower.Application.Modules.Audit.Handlers;

namespace Watchtower.Application.Modules.Audit;

/// <summary>JSON serializer context for Audit module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AuthEventDto))]
[JsonSerializable(typeof(ListAuthEvents.Query), TypeInfoPropertyName = "ListAuthEventsQuery")]
[JsonSerializable(typeof(ListAuthEvents.Response), TypeInfoPropertyName = "ListAuthEventsResponse")]
[JsonSerializable(typeof(ListAuthEventKinds.Query), TypeInfoPropertyName = "ListAuthEventKindsQuery")]
[JsonSerializable(typeof(ListAuthEventKinds.Response), TypeInfoPropertyName = "ListAuthEventKindsResponse")]
[JsonSerializable(typeof(AuditEventDto))]
[JsonSerializable(typeof(ListAuditEvents.Query), TypeInfoPropertyName = "ListAuditEventsQuery")]
[JsonSerializable(typeof(ListAuditEvents.Response), TypeInfoPropertyName = "ListAuditEventsResponse")]
public sealed partial class AuditJsonContext : JsonSerializerContext;
