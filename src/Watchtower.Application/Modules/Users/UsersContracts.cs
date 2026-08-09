using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Elarion.Abstractions.Identity;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Users;

/// <summary>
/// The public projection of a <see cref="User"/>. Deliberately carries no
/// <see cref="User.PasswordHash"/>, <see cref="User.SecurityStamp"/> or
/// <see cref="User.ConcurrencyStamp"/>: an administration screen has no use for them, and a response
/// shape that cannot carry a secret cannot leak one in a future refactor.
/// </summary>
/// <param name="LockedOut">
/// Derived from <see cref="User.LockoutEnd"/> against the current time rather than stored — a lockout
/// simply lapses, so a persisted flag would be stale the moment it expired.
/// </param>
public sealed record UserDto(
    int Id,
    string UserName,
    string? Email,
    bool IsAdmin,
    bool Disabled,
    bool LockedOut,
    DateTimeOffset CreatedAt);

/// <summary>
/// In-memory projection and the two rules every write handler in this module shares: the last-admin
/// guard and the audit trail.
/// </summary>
public static class UserMapping {
    /// <summary>Projects a user for the API. <paramref name="now"/> decides <see cref="UserDto.LockedOut"/>.</summary>
    /// <remarks>
    /// Applied after materialization rather than inside the EF projection on purpose: EF Core's SQLite
    /// provider cannot translate a <see cref="DateTimeOffset"/> comparison at all (SQLite has no date
    /// type), so <c>u.LockoutEnd &gt; now</c> would throw at translation time.
    /// </remarks>
    public static UserDto ToDto(User user, DateTimeOffset now) {
        ArgumentNullException.ThrowIfNull(user);
        return new UserDto(
            user.Id,
            user.UserName,
            user.Email,
            user.IsAdmin,
            user.Disabled,
            user.LockoutEnd is { } end && end > now,
            user.CreatedAt);
    }

    /// <summary>Normalizes an optional email: trimmed, or null when blank.</summary>
    public static string? NormalizeEmail(string? email) {
        var value = email?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Joins an <see cref="IdentityResult"/>'s failures into one message for <c>AppError.Validation</c>.</summary>
    public static string Describe(IdentityResult result) {
        ArgumentNullException.ThrowIfNull(result);
        return result.Errors.Any()
            ? string.Join(" ", result.Errors.Select(e => e.Description))
            : "The account could not be saved.";
    }

    /// <summary>
    /// True when <paramref name="target"/> is the only administrator that can still sign in, so the
    /// caller's change (demotion, disable, delete) would leave the instance with none.
    /// </summary>
    /// <remarks>
    /// This is the <em>only</em> protection the module applies. Demoting, disabling or deleting your
    /// own account is deliberately allowed: an operator with a second administrator account has every
    /// right to retire the first, and a self-targeting ban would just be an extra rule to work around.
    /// A disabled administrator is already unable to sign in, so it is not counted — and changing one
    /// is therefore never blocked.
    /// <para>
    /// The break-glass <c>WATCHTOWER__AUTH__RESETPASSWORD</c> hook (design.md §11) remains the recovery
    /// path if an instance ends up with no usable administrator anyway.
    /// </para>
    /// </remarks>
    public static async Task<bool> IsLastUsableAdminAsync(
        WatchtowerDbContext db, User target, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAdmin || target.Disabled) return false;
        return !await db.Users.AnyAsync(u => u.Id != target.Id && u.IsAdmin && !u.Disabled, ct);
    }

    /// <summary>The refusal returned when <see cref="IsLastUsableAdminAsync"/> holds.</summary>
    public static AppError LastAdminError(string action, User target) {
        ArgumentNullException.ThrowIfNull(target);
        return AppError.BusinessRule(
            $"Cannot {action} '{target.UserName}': it is the last administrator that can still sign in. " +
            "Grant another account the Admin role first.");
    }

    /// <summary>
    /// Appends an <see cref="AuthEvent"/> and saves it. Kinds are the dotted identifiers of
    /// design.md §9 (<c>user.created</c>, <c>user.deleted</c>, …).
    /// </summary>
    /// <remarks>
    /// <paramref name="details"/> always names the target by id and login name because
    /// <see cref="AuthEvent.UserId"/> is <c>SET NULL</c> on delete — the row that records an account
    /// being removed would otherwise be the one row that no longer says whose account it was.
    /// </remarks>
    public static async Task RecordAsync(
        WatchtowerDbContext db,
        ICurrentUser actor,
        TimeProvider time,
        string kind,
        User target,
        string? details,
        CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(target);

        var actorId = string.IsNullOrEmpty(actor.UserId) ? "unknown" : actor.UserId;
        var detail = $"actor={actorId}; target={target.UserName}#{target.Id}";
        if (!string.IsNullOrEmpty(details)) detail = $"{detail}; {details}";

        db.AuthEvents.Add(new AuthEvent {
            Kind = kind,
            UserId = target.Id,
            Detail = detail,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(ct);
    }
}
