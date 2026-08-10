using System.Text.Json.Serialization;
using Watchtower.Application.Modules.Realms.Handlers;

namespace Watchtower.Application.Modules.Realms;

/// <summary>JSON serializer context for Realms module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(RealmDto))]
[JsonSerializable(typeof(ListRealms.Query), TypeInfoPropertyName = "ListRealmsQuery")]
[JsonSerializable(typeof(ListRealms.Response), TypeInfoPropertyName = "ListRealmsResponse")]
[JsonSerializable(typeof(CreateRealm.Command), TypeInfoPropertyName = "CreateRealmCommand")]
[JsonSerializable(typeof(CreateRealm.Response), TypeInfoPropertyName = "CreateRealmResponse")]
[JsonSerializable(typeof(UpdateRealm.Command), TypeInfoPropertyName = "UpdateRealmCommand")]
[JsonSerializable(typeof(UpdateRealm.Response), TypeInfoPropertyName = "UpdateRealmResponse")]
[JsonSerializable(typeof(DeleteRealm.Command), TypeInfoPropertyName = "DeleteRealmCommand")]
[JsonSerializable(typeof(DeleteRealm.Response), TypeInfoPropertyName = "DeleteRealmResponse")]
public sealed partial class RealmsJsonContext : JsonSerializerContext;
