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

        using var scope = scopeFactory.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        await EnsureAdminAsync(users, db, auth, cancellationToken);
        await ApplyBreakGlassResetAsync(users, auth, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Creates the <c>admin</c> account when the instance has no users at all.</summary>
    private async Task EnsureAdminAsync(
        UserManager<User> users, WatchtowerDbContext db, AuthOptions auth, CancellationToken ct) {
        if (await db.Users.AnyAsync(ct)) return;

        var generated = string.IsNullOrWhiteSpace(auth.BootstrapPassword);
        var password = generated ? GeneratePassword() : auth.BootstrapPassword!;

        var admin = new User {
            UserName = AdminUserName,
            NormalizedUserName = AdminUserName.ToUpperInvariant(),
            PasswordHash = string.Empty,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            IsAdmin = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var result = await users.CreateAsync(admin, password);
        if (!result.Succeeded) {
            logger.LogError(
                "Could not create the initial '{UserName}' account: {Errors}. " +
                "Fix WATCHTOWER__AUTH__BOOTSTRAPPASSWORD and restart.",
                AdminUserName, Describe(result));
            return;
        }

        if (generated) {
            logger.LogWarning(
                "Created the initial Watchtower administrator '{UserName}' with the generated password: {Password}\n" +
                "This is the only time it is shown — save it now, then change it or set " +
                "WATCHTOWER__AUTH__BOOTSTRAPPASSWORD.",
                AdminUserName, password);
        } else {
            logger.LogInformation(
                "Created the initial Watchtower administrator '{UserName}' using the configured bootstrap password.",
                AdminUserName);
        }
    }

    /// <summary>
    /// Recovery hook for a locked-out operator: resets the <c>admin</c> password to
    /// <see cref="AuthOptions.ResetPassword"/> and clears any lockout. Applied on every start, so the
    /// setting is meant to be removed again once access is restored.
    /// </summary>
    private async Task ApplyBreakGlassResetAsync(UserManager<User> users, AuthOptions auth, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(auth.ResetPassword)) return;
        ct.ThrowIfCancellationRequested();

        var admin = await users.FindByNameAsync(AdminUserName);
        if (admin is null) {
            logger.LogWarning(
                "WATCHTOWER__AUTH__RESETPASSWORD is set but there is no '{UserName}' account to reset.",
                AdminUserName);
            return;
        }

        var removed = await users.RemovePasswordAsync(admin);
        if (!removed.Succeeded) {
            logger.LogError("Break-glass reset failed while clearing the old password: {Errors}", Describe(removed));
            return;
        }

        var added = await users.AddPasswordAsync(admin, auth.ResetPassword!);
        if (!added.Succeeded) {
            logger.LogError(
                "Break-glass reset failed: {Errors}. The '{UserName}' account now has no password — " +
                "correct WATCHTOWER__AUTH__RESETPASSWORD and restart.",
                Describe(added), AdminUserName);
            return;
        }

        await users.SetLockoutEndDateAsync(admin, null);
        await users.ResetAccessFailedCountAsync(admin);

        logger.LogWarning(
            "Break-glass reset applied: the '{UserName}' password was reset from configuration and its lockout " +
            "cleared. Remove WATCHTOWER__AUTH__RESETPASSWORD once you are signed in.",
            AdminUserName);
    }

    private static string GeneratePassword() =>
        RandomNumberGenerator.GetString(PasswordAlphabet, GeneratedPasswordLength);

    private static string Describe(IdentityResult result) =>
        string.Join("; ", result.Errors.Select(e => e.Description));
}
