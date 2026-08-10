using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Makes the first login possible without an interactive setup step: on startup, if central
/// authorization is enabled and no account exists yet, creates the <c>admin</c> user — and applies the
/// break-glass password reset when one is configured (docs/central-auth/design.md §2.6, §11).
/// </summary>
/// <remarks>
/// Runs after <c>Program.InitializeDatabaseAsync</c> has migrated the schema, and is a no-op unless
/// <see cref="AuthOptions.Enabled"/>. Being a singleton it resolves <see cref="UserManager{TUser}"/>
/// through a short-lived scope rather than capturing the scoped context.
/// </remarks>
public sealed class AuthBootstrapService(
    IServiceScopeFactory scopeFactory,
    IOptions<WatchtowerOptions> options,
    TimeProvider time,
    ILogger<AuthBootstrapService> logger) : IHostedService {

    /// <summary>Login name of the account created on first start.</summary>
    public const string AdminUserName = "admin";

    /// <summary>Length of a generated bootstrap password (well past the 10-character policy minimum).</summary>
    private const int GeneratedPasswordLength = 24;

    /// <summary>Alphabet for generated passwords: no characters that are ambiguous when read off a log.</summary>
    private const string PasswordAlphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task StartAsync(CancellationToken cancellationToken) {
        var auth = options.Value.Auth;
        if (!auth.Enabled) return;

        try {
            using var scope = scopeFactory.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var sessions = scope.ServiceProvider.GetRequiredService<AuthSessionService>();

            // Break-glass runs first: on an instance that has no administrator it creates one, which
            // then makes the ordinary first-run bootstrap a no-op instead of a competing account.
            await ApplyBreakGlassResetAsync(users, sessions, db, auth, cancellationToken);
            await EnsureAdminAsync(users, db, auth, cancellationToken);
        } catch (Exception ex) {
            // Never take the host down: a transient database problem here must not turn into a boot
            // loop that also removes the operator's ability to look at the UI and fix it.
            logger.LogError(
                ex, "Auth bootstrap failed; Watchtower is starting without verifying the '{UserName}' account. " +
                    "Restart once the database is reachable, or set WATCHTOWER__AUTH__RESETPASSWORD to recover.",
                AdminUserName);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Creates the <c>admin</c> account when the instance has no operator accounts at all.</summary>
    /// <remarks>
    /// Scoped to the system realm (docs/central-auth/design.md §13), not to the users table as a whole: a
    /// deployment whose only accounts live in a customer realm still has nobody who can administer it, and
    /// a bootstrap that counted those would leave it with no way in.
    /// </remarks>
    private async Task EnsureAdminAsync(
        UserManager<User> users, WatchtowerDbContext db, AuthOptions auth, CancellationToken ct) {
        if (await db.Users.AnyAsync(u => u.RealmId == Realm.SystemRealmId, ct)) return;

        var generated = string.IsNullOrWhiteSpace(auth.BootstrapPassword);
        var password = generated ? GeneratePassword() : auth.BootstrapPassword!;

        var result = await users.CreateAsync(NewAdmin(), password);
        if (!result.Succeeded) {
            logger.LogError(
                "Could not create the initial '{UserName}' account: {Errors}. " +
                "Fix WATCHTOWER__AUTH__BOOTSTRAPPASSWORD and restart.",
                AdminUserName, Describe(result));
            return;
        }

        if (generated) {
            logger.LogWarning(
                "Created the initial Watchtower administrator '{UserName}' with the generated password: {Password} " +
                "— this is the only time it is shown, so save it now, then change it or set " +
                "WATCHTOWER__AUTH__BOOTSTRAPPASSWORD.",
                AdminUserName, password);
        } else {
            logger.LogInformation(
                "Created the initial Watchtower administrator '{UserName}' using the configured bootstrap password.",
                AdminUserName);
        }
    }

    /// <summary>
    /// Recovery hook for a locked-out operator: guarantees that after this start there is an <c>admin</c>
    /// account whose password is <see cref="AuthOptions.ResetPassword"/> and which is not locked out —
    /// recreating the account if it was renamed or deleted. Applied on every start, so the setting is
    /// meant to be removed again once access is restored.
    /// </summary>
    /// <remarks>
    /// The reset goes through a password-reset token rather than remove-then-add: <c>ResetPasswordAsync</c>
    /// runs the password validators <em>before</em> touching the stored hash, so a
    /// <c>ResetPassword</c> that violates the policy leaves the existing password working instead of
    /// wiping it and locking the operator out for good.
    /// <para>
    /// A successful reset also revokes the account's existing sessions. Break-glass is used when control of
    /// the account is in doubt, and a session issued before the reset would otherwise keep working for up to
    /// the absolute lifetime — changing the password would not have taken the intruder's access away.
    /// </para>
    /// </remarks>
    private async Task ApplyBreakGlassResetAsync(
        UserManager<User> users, AuthSessionService sessions, WatchtowerDbContext db, AuthOptions auth,
        CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(auth.ResetPassword)) return;
        ct.ThrowIfCancellationRequested();

        var admin = await users.FindByNameAsync(AdminUserName);
        if (admin is null) {
            var newAdmin = NewAdmin();
            var created = await users.CreateAsync(newAdmin, auth.ResetPassword!);
            if (created.Succeeded) {
                await RecordBreakGlassAsync(db, newAdmin.Id, "created admin account (none existed)", ct);
                logger.LogWarning(
                    "Break-glass recovery: there was no '{UserName}' account, so one was created from " +
                    "WATCHTOWER__AUTH__RESETPASSWORD. Remove that setting once you are signed in.",
                    AdminUserName);
            } else {
                logger.LogError(
                    "Break-glass recovery could not create the '{UserName}' account: {Errors}. " +
                    "Correct WATCHTOWER__AUTH__RESETPASSWORD and restart.",
                    AdminUserName, Describe(created));
            }
            return;
        }

        var token = await users.GeneratePasswordResetTokenAsync(admin);
        var reset = await users.ResetPasswordAsync(admin, token, auth.ResetPassword!);
        if (!reset.Succeeded) {
            logger.LogError(
                "Break-glass reset rejected for '{UserName}': {Errors}. The previous password is unchanged and " +
                "still valid — correct WATCHTOWER__AUTH__RESETPASSWORD and restart.",
                AdminUserName, Describe(reset));
            return;
        }

        await users.SetLockoutEndDateAsync(admin, null);
        await users.ResetAccessFailedCountAsync(admin);

        // Recovery is only recovery if it also ends whatever sessions are already out there.
        var revoked = await sessions.RevokeAllForUserAsync(admin.Id, ct);

        await RecordBreakGlassAsync(
            db, admin.Id, $"reset admin password, cleared lockout, revoked {revoked} session(s)", ct);
        logger.LogWarning(
            "Break-glass reset applied: the '{UserName}' password was reset from configuration, its lockout " +
            "cleared, and {Revoked} existing session(s) revoked. Remove WATCHTOWER__AUTH__RESETPASSWORD once " +
            "you are signed in.",
            AdminUserName, revoked);
    }

    /// <summary>
    /// Writes the audit row for a successful break-glass recovery. The trail must show that access was
    /// restored out of band; it is committed on its own <see cref="CancellationToken.None"/> so a shutdown
    /// racing startup cannot drop the record of a recovery that did happen.
    /// </summary>
    private async Task RecordBreakGlassAsync(WatchtowerDbContext db, int adminId, string detail, CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        db.AuthEvents.Add(new AuthEvent {
            Kind = AuthEventKinds.BreakGlass,
            UserId = adminId,
            Detail = detail,
            CreatedAt = time.GetUtcNow(),
        });
        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>
    /// A fresh administrator; <see cref="UserManager{TUser}"/> fills in the normalized name, hash and
    /// stamps. The realm is stated rather than left to the entity default: this account is the operator
    /// population's only way in, and which population it lands in is the whole point.
    /// </summary>
    private static User NewAdmin() => new() {
        RealmId = Realm.SystemRealmId,
        UserName = AdminUserName,
        NormalizedUserName = AdminUserName.ToUpperInvariant(),
        PasswordHash = string.Empty,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        IsAdmin = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static string GeneratePassword() =>
        RandomNumberGenerator.GetString(PasswordAlphabet, GeneratedPasswordLength);

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => e.Description));
}
