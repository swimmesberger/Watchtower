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
/// <see cref="User.PasswordHash"/>. Every <see cref="IdentityResult"/> failure (policy violation,
/// disallowed characters, a name already taken) becomes a single <c>Validation</c> error, because
/// from the caller's side they are all "the form is wrong"; a duplicate name in particular is
/// reported by <see cref="WatchtowerUserStore"/> as <c>DuplicateUserName</c> whether it lost the
/// race or simply came second.
/// </remarks>
[Handler("users.create")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class CreateUser(
    WatchtowerDbContext db,
    UserManager<User> users,
    ICurrentUser currentUser,
    TimeProvider time)
    : IHandler<CreateUser.Command, Result<CreateUser.Response>> {

    public sealed record Command(string UserName, string Password, string? Email, bool IsAdmin);
    public sealed record Response(UserDto User);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var userName = command.UserName?.Trim();
        if (string.IsNullOrEmpty(userName))
            return AppError.Validation("User name is required.");
        if (string.IsNullOrEmpty(command.Password))
            return AppError.Validation("Password is required.");

        var now = time.GetUtcNow();
        // NormalizedUserName, PasswordHash and SecurityStamp are placeholders: UserManager and the
        // store overwrite all three on the way in. They are `required` on the entity, not optional.
        var user = new User {
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
            return AppError.Validation(UserMapping.Describe(result));

        await UserMapping.RecordAsync(
            db, currentUser, time, "user.created", user, $"isAdmin={command.IsAdmin}", ct);

        return new Response(UserMapping.ToDto(user, now));
    }
}
