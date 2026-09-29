using System.Text.Json.Serialization;
using Watchtower.Application.Modules.Notifications.Handlers;
using Watchtower.Application.Modules.Notifications.Services;

namespace Watchtower.Application.Modules.Notifications;

/// <summary>JSON serializer context for Notifications module request/response types and the push payload.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(GetPushPublicKey.Query), TypeInfoPropertyName = "GetPushPublicKeyQuery")]
[JsonSerializable(typeof(GetPushPublicKey.Response), TypeInfoPropertyName = "GetPushPublicKeyResponse")]
[JsonSerializable(typeof(SubscribePush.Command), TypeInfoPropertyName = "SubscribePushCommand")]
[JsonSerializable(typeof(SubscribePush.Response), TypeInfoPropertyName = "SubscribePushResponse")]
[JsonSerializable(typeof(UnsubscribePush.Command), TypeInfoPropertyName = "UnsubscribePushCommand")]
[JsonSerializable(typeof(UnsubscribePush.Response), TypeInfoPropertyName = "UnsubscribePushResponse")]
[JsonSerializable(typeof(SendTestPush.Command), TypeInfoPropertyName = "SendTestPushCommand")]
[JsonSerializable(typeof(SendTestPush.Response), TypeInfoPropertyName = "SendTestPushResponse")]
[JsonSerializable(typeof(PushPayload))]
public sealed partial class NotificationsJsonContext : JsonSerializerContext;
