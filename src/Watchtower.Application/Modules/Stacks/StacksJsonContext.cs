using System.Text.Json.Serialization;
using Watchtower.Application.Modules.Stacks.Handlers;

namespace Watchtower.Application.Modules.Stacks;

/// <summary>JSON serializer context for Stacks module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(StackDto))]
[JsonSerializable(typeof(DeployEventDto))]
[JsonSerializable(typeof(StackEnvVarDto))]
[JsonSerializable(typeof(StackEnvVarInput))]
[JsonSerializable(typeof(DeployAcceptedDto))]
[JsonSerializable(typeof(ListStacks.Query), TypeInfoPropertyName = "ListStacksQuery")]
[JsonSerializable(typeof(ListStacks.Response), TypeInfoPropertyName = "ListStacksResponse")]
[JsonSerializable(typeof(GetStack.Query), TypeInfoPropertyName = "GetStackQuery")]
[JsonSerializable(typeof(GetStack.Response), TypeInfoPropertyName = "GetStackResponse")]
[JsonSerializable(typeof(CreateStack.Command), TypeInfoPropertyName = "CreateStackCommand")]
[JsonSerializable(typeof(CreateStack.Response), TypeInfoPropertyName = "CreateStackResponse")]
[JsonSerializable(typeof(UpdateStack.Command), TypeInfoPropertyName = "UpdateStackCommand")]
[JsonSerializable(typeof(UpdateStack.Response), TypeInfoPropertyName = "UpdateStackResponse")]
[JsonSerializable(typeof(DeleteStack.Command), TypeInfoPropertyName = "DeleteStackCommand")]
[JsonSerializable(typeof(DeleteStack.Response), TypeInfoPropertyName = "DeleteStackResponse")]
[JsonSerializable(typeof(DeployStack.Command), TypeInfoPropertyName = "DeployStackCommand")]
[JsonSerializable(typeof(DeployStack.Response), TypeInfoPropertyName = "DeployStackResponse")]
[JsonSerializable(typeof(ListDeployEvents.Query), TypeInfoPropertyName = "ListDeployEventsQuery")]
[JsonSerializable(typeof(ListDeployEvents.Response), TypeInfoPropertyName = "ListDeployEventsResponse")]
[JsonSerializable(typeof(GetStackEnv.Query), TypeInfoPropertyName = "GetStackEnvQuery")]
[JsonSerializable(typeof(GetStackEnv.Response), TypeInfoPropertyName = "GetStackEnvResponse")]
[JsonSerializable(typeof(SetStackEnv.Command), TypeInfoPropertyName = "SetStackEnvCommand")]
[JsonSerializable(typeof(SetStackEnv.Response), TypeInfoPropertyName = "SetStackEnvResponse")]
[JsonSerializable(typeof(CheckStackUpdates.Command), TypeInfoPropertyName = "CheckStackUpdatesCommand")]
[JsonSerializable(typeof(CheckStackUpdates.Response), TypeInfoPropertyName = "CheckStackUpdatesResponse")]
public sealed partial class StacksJsonContext : JsonSerializerContext;
