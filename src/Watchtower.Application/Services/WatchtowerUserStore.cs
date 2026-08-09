using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Backs ASP.NET Identity <em>core</em> (<see cref="UserManager{TUser}"/>, the password hasher, the
/// lockout and security-stamp machinery) with Watchtower's own <see cref="User"/> entity and
/// <see cref="WatchtowerDbContext"/> — deliberately instead of <c>IdentityDbContext</c>, whose DbSets
/// would collide with the source-generated ones (docs/central-auth/design.md §2.4).
/// </summary>
/// <remarks>
/// Scoped, like the context it writes through. Names arriving at
/// <see cref="FindByNameAsync"/> and <see cref="SetNormalizedUserNameAsync"/> have already been put
/// through Identity's <c>ILookupNormalizer</c> by <see cref="UserManager{TUser}"/>, which is what
/// makes login case-insensitive on SQLite.
/// </remarks>
public sealed class WatchtowerUserStore(WatchtowerDbContext db, IdentityErrorDescriber errors) :
    IUserStore<User>,
    IUserPasswordStore<User>,
    IUserSecurityStampStore<User>,
    IUserLockoutStore<User> {

    /// <summary>SQLite extended result code <c>SQLITE_CONSTRAINT_UNIQUE</c>.</summary>
    private const int SqliteConstraintUnique = 2067;

    /// <summary>SQLite extended result code <c>SQLITE_CONSTRAINT_PRIMARYKEY</c>.</summary>
    private const int SqliteConstraintPrimaryKey = 1555;

    // -- Identity --------------------------------------------------------------------------------

    public Task<string> GetUserIdAsync(User user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.Id.ToString(CultureInfo.InvariantCulture));
    }

    public Task<string?> GetUserNameAsync(User user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult<string?>(user.UserName);
    }

    public Task SetUserNameAsync(User user, string? userName, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        user.UserName = userName ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(User user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult<string?>(user.NormalizedUserName);
    }

    public Task SetNormalizedUserNameAsync(User user, string? normalizedName, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        user.NormalizedUserName = normalizedName ?? string.Empty;
        return Task.CompletedTask;
    }

    // -- Persistence -----------------------------------------------------------------------------

    public async Task<IdentityResult> CreateAsync(User user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        if (user.CreatedAt == default) user.CreatedAt = DateTimeOffset.UtcNow;
        if (string.IsNullOrEmpty(user.ConcurrencyStamp)) user.ConcurrencyStamp = NewStamp();
        db.Users.Add(user);
        try {
            await db.SaveChangesAsync(cancellationToken);
        } catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex)) {
            // UserManager's uniqueness check is a separate read, so two concurrent creations of the
            // same name both pass it and the unique index is what actually decides. Only that specific
            // constraint failure is a domain error — anything else is an environment or code fault and
            // must keep propagating as an exception.
            db.Entry(user).State = EntityState.Detached;
            return IdentityResult.Failed(errors.DuplicateUserName(user.UserName));
        }
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(User user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        DetachOtherInstanceOf(user);
        // Attach before rotating so the *stored* stamp is what lands in the UPDATE's WHERE clause —
        // rotating first would compare the row against the new value and never match.
        db.Users.Attach(user);
        user.ConcurrencyStamp = NewStamp();
        db.Users.Update(user);
        try {
            await db.SaveChangesAsync(cancellationToken);
        } catch (DbUpdateConcurrencyException) {
            return IdentityResult.Failed(errors.ConcurrencyFailure());
        }
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(User user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        cancellationToken.ThrowIfCancellationRequested();
        DetachOtherInstanceOf(user);
        db.Users.Remove(user);
        try {
            await db.SaveChangesAsync(cancellationToken);
        } catch (DbUpdateConcurrencyException) {
            return IdentityResult.Failed(errors.ConcurrencyFailure());
        }
        return IdentityResult.Success;
    }

    public async Task<User?> FindByIdAsync(string userId, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return int.TryParse(userId, out var id)
            ? await db.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
            : null;
    }

    public async Task<User?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        return await db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedUserName, cancellationToken);
    }

    // -- Password --------------------------------------------------------------------------------

    /// <summary>
    /// Identity models "no password" as null; the column is non-nullable, so it is stored as the empty
    /// string and reported back as null by <see cref="GetPasswordHashAsync"/>. Anything else makes
    /// <c>UserManager.AddPasswordAsync</c> believe a cleared account still has a password.
    /// </summary>
    public Task SetPasswordHashAsync(User user, string? passwordHash, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        user.PasswordHash = passwordHash ?? string.Empty;
        return Task.CompletedTask;
    }

    /// <inheritdoc cref="SetPasswordHashAsync"/>
    public Task<string?> GetPasswordHashAsync(User user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(string.IsNullOrEmpty(user.PasswordHash) ? null : user.PasswordHash);
    }

    public Task<bool> HasPasswordAsync(User user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));
    }

    // -- Security stamp --------------------------------------------------------------------------

    public Task SetSecurityStampAsync(User user, string stamp, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        user.SecurityStamp = stamp;
        return Task.CompletedTask;
    }

    public Task<string?> GetSecurityStampAsync(User user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult<string?>(user.SecurityStamp);
    }

    // -- Lockout ---------------------------------------------------------------------------------

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(User user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.LockoutEnd);
    }

    public Task SetLockoutEndDateAsync(User user, DateTimeOffset? lockoutEnd, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(User user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(++user.AccessFailedCount);
    }

    public Task ResetAccessFailedCountAsync(User user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    public Task<int> GetAccessFailedCountAsync(User user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(user);
        return Task.FromResult(user.AccessFailedCount);
    }

    /// <summary>
    /// Always true: brute-force protection is a Watchtower-wide policy, not a per-account opt-out, so
    /// there is no column behind it (and <see cref="SetLockoutEnabledAsync"/> is therefore a no-op).
    /// </summary>
    public Task<bool> GetLockoutEnabledAsync(User user, CancellationToken cancellationToken) =>
        Task.FromResult(true);

    /// <inheritdoc cref="GetLockoutEnabledAsync"/>
    public Task SetLockoutEnabledAsync(User user, bool enabled, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>No-op: the <see cref="WatchtowerDbContext"/> is owned by the DI scope, not by this store.</summary>
    public void Dispose() { }

    // -- Helpers ---------------------------------------------------------------------------------

    private static string NewStamp() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// Lets a caller write back a user it loaded detached (e.g. via <c>AsNoTracking</c>). Identity's own
    /// <c>UserValidator</c> calls <see cref="FindByNameAsync"/> on the way into an update, which tracks a
    /// <em>second</em> instance of the same row; attaching the caller's copy on top of that throws.
    /// Evicting the validator's copy makes the caller's instance — and its original concurrency stamp —
    /// the one that wins.
    /// </summary>
    private void DetachOtherInstanceOf(User user) {
        if (user.Id == 0) return;
        var tracked = db.Users.Local.FirstOrDefault(u => u.Id == user.Id && !ReferenceEquals(u, user));
        if (tracked is not null) db.Entry(tracked).State = EntityState.Detached;
    }

    /// <summary>
    /// True only for a violated unique/primary-key index — the one database failure that represents a
    /// domain outcome (a name already taken) rather than a fault worth throwing.
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException {
            SqliteExtendedErrorCode: SqliteConstraintUnique or SqliteConstraintPrimaryKey,
        };
}
