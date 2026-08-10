using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Microsoft.AspNetCore.Identity;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Users.Handlers;

/// <summary>
/// Creates a local account with an initial password.
/// </summary>
/// <remarks>
/// The password goes through <see cref="UserManager{TUser}.CreateAsync(TUser, string)"/> so the
/// configured policy and the PBKDF2 hasher apply — the handler never touches
/// <see cref="User.PasswordHash"/>. Failures are mapped by <see cref="UserMapping.ToError"/>: a policy
/// violation, a disallowed character or a name already taken are all "the form is wrong" and become one
/// <c>Validation</c> error. A duplicate name in particular is reported by
/// <see cref="WatchtowerUserStore"/> as <c>DuplicateUserName</c> whether it lost the race to the unique
/// index or simply came second.
/// </remarks>
[Handler("users.create")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class CreateUser(
    WatchtowerDbContext db,
    UserManager<User> users,
    IRealmContext realmContext,
    ICurrentUser currentUser,
    TimeProvider time)
    : IHandler<CreateUser.Command, Result<CreateUser.Response>> {

    /// <summary>
    /// <paramref name="RealmId"/> is optional and last (a default value is what marks a parameter
    /// non-required in the generated schema): a client that predates realms omits it and creates an
    /// operator account, exactly as it always did.
    /// </summary>
    public sealed record Command(
        string UserName, string Password, string? Email, bool IsAdmin, int? RealmId = null);

    public sealed record Response(UserDto User);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var userName = command.UserName?.Trim();
        if (string.IsNullOrEmpty(userName))
            return AppError.Validation("User name is required.");
        if (string.IsNullOrEmpty(command.Password))
            return AppError.Validation("Password is required.");

        var realmId = command.RealmId ?? Realm.SystemRealmId;
        if (await UserMapping.FindRealmAsync(db, realmId, ct) is null)
            return AppError.Validation($"No realm exists with id {realmId}.");
        if (UserMapping.ValidateAdminRealm(command.IsAdmin, realmId) is { } badRole)
            return badRole;

        // Before UserManager touches anything: the duplicate-name check it runs has to be answered about
        // the realm this account is going into, not about the scope's default (design.md §13).
        realmContext.SetRealm(realmId);

        var now = time.GetUtcNow();
        // NormalizedUserName, PasswordHash and SecurityStamp are placeholders: UserManager and the
        // store overwrite all three on the way in. They are `required` on the entity, not optional.
        var user = new User {
            RealmId = realmId,
            UserName = userName,
            NormalizedUserName = userName.ToUpperInvariant(),
            Email = UserMapping.NormalizeEmail(command.Email),
            PasswordHash = string.Empty,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            IsAdmin = command.IsAdmin,
            CreatedAt = now,
        };

        var result = await users.CreateAsync(user, command.Password);
        if (!result.Succeeded)
            return UserMapping.ToError(result);

        // Past the commit point: the account exists, so the trail is written uncancellably.
        await UserMapping.RecordAsync(
            db, currentUser, time, AuthEventKinds.UserCreated, user,
            $"isAdmin={command.IsAdmin}; realmId={realmId}");

        return new Response(UserMapping.ToDto(user, now));
    }
}
