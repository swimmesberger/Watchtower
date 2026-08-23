using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Elarion.Abstractions.Identity;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

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
/// <param name="RealmId">
/// The population the account belongs to (docs/central-auth/design.md §13). Login names are unique within
/// it, not across the instance, so an administration screen listing two accounts called <c>admin</c> needs
/// this to tell them apart.
/// </param>
/// <param name="TwoFactorEnabled">
/// Whether the account demands an authenticator code after its password. Safe to project — it is a policy
/// fact, not a secret, and the administrator who may reset it needs to know whether there is anything to
/// reset. The authenticator key and the recovery-code hashes are the secrets, and neither appears here or
/// in any other response.
/// </param>
public sealed record UserDto(
    int Id,
    string UserName,
    string? Email,
    bool IsAdmin,
    bool Disabled,
    bool LockedOut,
    bool TwoFactorEnabled,
    int RealmId,
    DateTimeOffset CreatedAt);

/// <summary>
/// In-memory projection and the two rules every write handler in this module shares: the last-admin
/// guard and the audit trail.
/// </summary>
public static class UserMapping {
    /// <summary>Projects an already-loaded user for the API. <paramref name="now"/> decides <see cref="UserDto.LockedOut"/>.</summary>
    /// <remarks>
    /// For the write handlers, which hold the tracked entity they just saved. The list handler projects
    /// the same shape in SQL instead — see <c>ListUsers</c>.
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
            user.TwoFactorEnabled,
            user.RealmId,
            user.CreatedAt);
    }

    /// <summary>
    /// Points the scope's credential space at <paramref name="user"/>'s realm before the account is written
    /// back (docs/central-auth/design.md §13).
    /// </summary>
    /// <remarks>
    /// Not a nicety: <see cref="UserManager{TUser}.UpdateAsync"/> runs Identity's <c>UserValidator</c>,
    /// which checks for a duplicate login name by calling the store's <c>FindByNameAsync</c> — and that
    /// lookup is realm-scoped. Leaving the default (the operator realm) in place while saving a customer
    /// realm's account would ask "is this name taken?" of the wrong population, and an unrelated operator
    /// account with the same name would be reported as a duplicate of it.
    /// </remarks>
    public static void PinRealm(IRealmContext realm, User user) {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(user);
        realm.SetRealm(user.RealmId);
    }

    /// <summary>
    /// Refuses the Admin role outside the operator realm, or <see langword="null"/> when the combination is
    /// allowed.
    /// </summary>
    /// <remarks>
    /// The role means administration of <em>the instance</em> — users, routes, stacks, realms — and a realm
    /// exists precisely so a customer population cannot reach any of that. Refused here so an operator gets
    /// a clear answer rather than a silently inert flag; <see cref="WatchtowerClaims.ForUser"/> then
    /// declines to emit the claim regardless, which is what makes the rule hold even for a row that
    /// acquired the flag some other way.
    /// </remarks>
    public static AppError? ValidateAdminRealm(bool isAdmin, int realmId) =>
        isAdmin && realmId != Realm.SystemRealmId
            ? AppError.Validation(
                "The Admin role administers the whole instance and can only be granted to an account in " +
                "the operator realm.")
            : null;

    /// <summary>The realm an account is being placed in, or <see langword="null"/> when there is no such realm.</summary>
    public static Task<Realm?> FindRealmAsync(WatchtowerDbContext db, int realmId, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(db);
        return db.Realms.AsNoTracking().FirstOrDefaultAsync(r => r.Id == realmId, ct);
    }

    /// <summary>Normalizes an optional email: trimmed, or null when blank.</summary>
    public static string? NormalizeEmail(string? email) {
        var value = email?.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Joins an <see cref="IdentityResult"/>'s failures into one message.</summary>
    public static string Describe(IdentityResult result) {
        ArgumentNullException.ThrowIfNull(result);
        return result.Errors.Any()
            ? string.Join(" ", result.Errors.Select(e => e.Description))
            : "The account could not be saved.";
    }

    /// <summary>
    /// The code Identity stamps on an optimistic-concurrency failure. Read off
    /// <see cref="IdentityErrorDescriber"/> rather than written out, so it cannot drift from what
    /// <see cref="Services.WatchtowerUserStore"/> actually returns, and so the match never depends on a
    /// localizable description string.
    /// </summary>
    private static readonly string ConcurrencyFailureCode =
        new IdentityErrorDescriber().ConcurrencyFailure().Code;

    /// <summary>
    /// Translates a failed <see cref="IdentityResult"/> into the error the caller sees.
    /// </summary>
    /// <remarks>
    /// A concurrency failure is the one outcome that is not the caller's input being wrong: another
    /// administrator wrote the same account first, and the answer is "re-read it and try again" —
    /// <c>Conflict</c>, not <c>Validation</c>. Everything else (policy violations, disallowed characters,
    /// a name already taken) is a bad request body.
    /// </remarks>
    public static AppError ToError(IdentityResult result) {
        ArgumentNullException.ThrowIfNull(result);
        return result.Errors.Any(e => string.Equals(e.Code, ConcurrencyFailureCode, StringComparison.Ordinal))
            ? AppError.Conflict(Describe(result))
            : AppError.Validation(Describe(result));
    }

    /// <summary>
    /// True when <paramref name="target"/> is the only administrator that can still sign in, so the
    /// caller's change (demotion, disable, delete) would leave the instance with none.
    /// </summary>
    /// <remarks>
    /// Demoting, disabling or deleting your <em>own</em> account is deliberately allowed: an operator with
    /// a second administrator account has every right to retire the first, and a self-targeting ban would
    /// just be an extra rule to work around. A disabled administrator cannot sign in, so it is not counted
    /// — and changing one is therefore never blocked.
    /// <para>
    /// <strong>This is a check-then-act guard, not a constraint.</strong> Two administrators demoting each
    /// other at the same moment can both read the other as still active and both succeed, and Watchtower
    /// has no ambient transaction that would make the pair atomic: the framework's
    /// <c>TransactionDecorator</c> is opt-in (it needs a <c>[DecoratorList]</c>, an <c>ICommand</c>-marked
    /// request and <c>AddElarionUnitOfWork</c>, none of which this application uses), so each
    /// <c>SaveChangesAsync</c> commits on its own. Closing the race properly would take a database-level
    /// constraint — "at least one row with <c>is_admin AND NOT disabled</c>" is not expressible as one, so
    /// it would mean a lock table or a serialized write path, which is a large amount of machinery for a
    /// millisecond-wide window between two trusted operators.
    /// </para>
    /// <para>
    /// The guard therefore stops the realistic mistake (one administrator retiring the last account) and
    /// not the concurrent one. The recovery path for an instance that ends up with no usable administrator
    /// is unchanged and always available: set <c>WATCHTOWER__AUTH__RESETPASSWORD</c> and restart
    /// (design.md §11) — <see cref="Services.AuthBootstrapService"/> recreates or unlocks the <c>admin</c>
    /// account on the next boot.
    /// </para>
    /// </remarks>
    public static async Task<bool> IsLastUsableAdminAsync(
        WatchtowerDbContext db, User target, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(target);
        if (!target.IsAdmin || target.Disabled) return false;
        // Scoped to the operator realm, which is the only realm the role can be held in
        // (<see cref="ValidateAdminRealm"/>) — stated rather than relied on, so a stray flag in another
        // realm cannot be counted as the administrator that keeps this instance reachable.
        return !await db.Users.AnyAsync(
            u => u.Id != target.Id && u.IsAdmin && !u.Disabled && u.RealmId == Realm.SystemRealmId, ct);
    }

    /// <summary>The refusal returned when <see cref="IsLastUsableAdminAsync"/> holds.</summary>
    public static AppError LastAdminError(string action, User target) {
        ArgumentNullException.ThrowIfNull(target);
        return AppError.BusinessRule(
            $"Cannot {action} '{target.UserName}': it is the last administrator that can still sign in. " +
            "Grant another account the Admin role first.");
    }

    /// <summary>
    /// Appends an <see cref="AuthEvent"/> and commits it. Kinds come from <see cref="AuthEventKinds"/>.
    /// </summary>
    /// <param name="targetRemoved">
    /// True when the account row has already been deleted. The event's <see cref="AuthEvent.UserId"/> is
    /// then left null — the column is <c>SET NULL</c> on delete, so pointing a <em>new</em> row at a gone
    /// user would simply violate the foreign key on insert.
    /// </param>
    /// <remarks>
    /// Takes no <see cref="CancellationToken"/> on purpose, and saves with
    /// <see cref="CancellationToken.None"/>. Every call site runs after the account change has already
    /// committed, so honouring the request token here would mean a caller that hangs up mid-request keeps
    /// its own administrative action out of the trail — the same reasoning (and the same mechanism) as the
    /// login endpoints' <c>RejectAsync</c>.
    /// <para>
    /// The detail always names the target by login name and id, so the one row that records an account
    /// being deleted is not also the one row that can no longer say whose account it was.
    /// </para>
    /// </remarks>
    public static async Task RecordAsync(
        WatchtowerDbContext db,
        ICurrentUser actor,
        TimeProvider time,
        string kind,
        User target,
        string? details,
        bool targetRemoved = false) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(target);

        var actorId = string.IsNullOrEmpty(actor.UserId) ? "unknown" : actor.UserId;
        var detail = $"actor={actorId}; target={target.UserName}#{target.Id}";
        if (!string.IsNullOrEmpty(details)) detail = $"{detail}; {details}";

        // Reference-free like every audit row: the target is named, so the row reads the same whether
        // the account still exists or was just deleted (targetRemoved) — the ids stay in the detail.
        db.AuditEvents.Add(new AuditEvent {
            Category = AuthEventKinds.CategoryOf(kind),
            Action = kind,
            Target = target.UserName ?? $"#{target.Id}",
            Detail = detail,
            Actor = await AuditLog.ResolveActorAsync(db, actor.UserId),
            Success = true,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
