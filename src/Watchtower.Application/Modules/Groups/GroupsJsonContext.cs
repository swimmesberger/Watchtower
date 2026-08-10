using System.Text.Json.Serialization;
using Watchtower.Application.Modules.Groups.Handlers;

namespace Watchtower.Application.Modules.Groups;

/// <summary>JSON serializer context for Groups module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(GroupDto))]
[JsonSerializable(typeof(ListGroups.Query), TypeInfoPropertyName = "ListGroupsQuery")]
[JsonSerializable(typeof(ListGroups.Response), TypeInfoPropertyName = "ListGroupsResponse")]
[JsonSerializable(typeof(CreateGroup.Command), TypeInfoPropertyName = "CreateGroupCommand")]
[JsonSerializable(typeof(CreateGroup.Response), TypeInfoPropertyName = "CreateGroupResponse")]
[JsonSerializable(typeof(RenameGroup.Command), TypeInfoPropertyName = "RenameGroupCommand")]
[JsonSerializable(typeof(RenameGroup.Response), TypeInfoPropertyName = "RenameGroupResponse")]
[JsonSerializable(typeof(DeleteGroup.Command), TypeInfoPropertyName = "DeleteGroupCommand")]
[JsonSerializable(typeof(DeleteGroup.Response), TypeInfoPropertyName = "DeleteGroupResponse")]
[JsonSerializable(typeof(GetGroupMembers.Query), TypeInfoPropertyName = "GetGroupMembersQuery")]
[JsonSerializable(typeof(GetGroupMembers.Response), TypeInfoPropertyName = "GetGroupMembersResponse")]
[JsonSerializable(typeof(SetGroupMembers.Command), TypeInfoPropertyName = "SetGroupMembersCommand")]
[JsonSerializable(typeof(SetGroupMembers.Response), TypeInfoPropertyName = "SetGroupMembersResponse")]
public sealed partial class GroupsJsonContext : JsonSerializerContext;
