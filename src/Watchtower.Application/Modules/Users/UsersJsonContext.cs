using System.Text.Json.Serialization;
using Watchtower.Application.Modules.Users.Handlers;

namespace Watchtower.Application.Modules.Users;

/// <summary>JSON serializer context for Users module request/response types.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(ListUsers.Query), TypeInfoPropertyName = "ListUsersQuery")]
[JsonSerializable(typeof(ListUsers.Response), TypeInfoPropertyName = "ListUsersResponse")]
[JsonSerializable(typeof(CreateUser.Command), TypeInfoPropertyName = "CreateUserCommand")]
[JsonSerializable(typeof(CreateUser.Response), TypeInfoPropertyName = "CreateUserResponse")]
[JsonSerializable(typeof(UpdateUser.Command), TypeInfoPropertyName = "UpdateUserCommand")]
[JsonSerializable(typeof(UpdateUser.Response), TypeInfoPropertyName = "UpdateUserResponse")]
[JsonSerializable(typeof(ResetUserPassword.Command), TypeInfoPropertyName = "ResetUserPasswordCommand")]
[JsonSerializable(typeof(ResetUserPassword.Response), TypeInfoPropertyName = "ResetUserPasswordResponse")]
[JsonSerializable(typeof(ResetUserMfa.Command), TypeInfoPropertyName = "ResetUserMfaCommand")]
[JsonSerializable(typeof(ResetUserMfa.Response), TypeInfoPropertyName = "ResetUserMfaResponse")]
[JsonSerializable(typeof(SetUserDisabled.Command), TypeInfoPropertyName = "SetUserDisabledCommand")]
[JsonSerializable(typeof(SetUserDisabled.Response), TypeInfoPropertyName = "SetUserDisabledResponse")]
[JsonSerializable(typeof(DeleteUser.Command), TypeInfoPropertyName = "DeleteUserCommand")]
[JsonSerializable(typeof(DeleteUser.Response), TypeInfoPropertyName = "DeleteUserResponse")]
public sealed partial class UsersJsonContext : JsonSerializerContext;
