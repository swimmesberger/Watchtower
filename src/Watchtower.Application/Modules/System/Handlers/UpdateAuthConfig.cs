using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Persists the auth settings as Global-scope settings under <c>Watchtower:Auth:*</c>. The session
/// lifetimes and login host re-bind at runtime; <c>Auth:Enabled</c> does <em>not</em> — it shapes the
/// pipeline before DI exists (<c>Program.cs</c>), so the response reports <c>RestartRequired</c> and the
/// stored value takes effect on the next start (the boot snapshot in <c>RuntimeSettingsLayering</c> is
/// what carries it there). Env-pinned paths (env wins over the store) are rejected when the request
/// tries to change them, and never written.
/// </summary>
/// <remarks>
/// Lockout guard: enabling requires an enabled admin account in the system realm. Without one, the
/// next restart would bootstrap an <c>admin</c> with a random password that only appears in the logs —
/// technically recoverable, practically a trap. The operator creates their account on the Users page
/// first (it is fully functional while auth is off), then flips the toggle. The env-var escape hatch
/// stays available either way: <c>WATCHTOWER__AUTH__ENABLED=false</c> + restart always wins over the
/// stored value.
/// </remarks>
[Handler("system.updateAuthConfig")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class UpdateAuthConfig(
    WatchtowerDbContext db,
    ISettingsManager settings,
    IOptionsMonitor<WatchtowerOptions> options,
    AuthStartupState startup,
    EnvironmentSettingPins pins,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<UpdateAuthConfig.Command, Result<UpdateAuthConfig.Response>> {
    public sealed record Command(
        bool Enabled,
        string? Host,
        int SessionLifetimeHours,
        int AbsoluteSessionLifetimeDays);

    public sealed record Response(
        bool Enabled,
        bool Active,
        bool RestartRequired,
        string? Host,
        int SessionLifetimeHours,
        int AbsoluteSessionLifetimeDays,
        string[] PinnedPaths);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (command.SessionLifetimeHours is < 1 or > 720)
            return AppError.Validation("SessionLifetimeHours must be between 1 and 720.");
        if (command.AbsoluteSessionLifetimeDays is < 1 or > 365)
            return AppError.Validation("AbsoluteSessionLifetimeDays must be between 1 and 365.");

        var host = command.Host?.Trim().ToLowerInvariant() ?? "";
        if (host.Length > 0 && (host.Contains("://", StringComparison.Ordinal) || host.Contains('/') || host.Contains(' ')))
            return AppError.Validation("Host must be a bare hostname (e.g. watchtower.example.com) — no scheme, path or spaces.");

        var auth = options.CurrentValue.Auth;
        var violations = new List<string>();
        void Check(string path, bool changed) {
            if (changed && pins.IsPinned(path)) violations.Add(path);
        }
        Check(WatchtowerSettingPaths.AuthEnabled, command.Enabled != auth.Enabled);
        Check(WatchtowerSettingPaths.AuthHost, !string.Equals(host, auth.Host?.Trim().ToLowerInvariant() ?? "", StringComparison.Ordinal));
        Check(WatchtowerSettingPaths.AuthSessionLifetimeHours, command.SessionLifetimeHours != auth.SessionLifetimeHours);
        Check(WatchtowerSettingPaths.AuthAbsoluteSessionLifetimeDays, command.AbsoluteSessionLifetimeDays != auth.AbsoluteSessionLifetimeDays);
        if (violations.Count > 0)
            return EnvironmentSettingPins.PinnedError(violations);

        if (command.Enabled) {
            var hasAdmin = await db.Users.AnyAsync(
                u => u.RealmId == Realm.SystemRealmId && u.IsAdmin && !u.Disabled, ct);
            if (!hasAdmin)
                return AppError.BusinessRule(
                    "Create an enabled admin account on the Users page before enabling authentication — " +
                    "enabling without one would lock you out after the restart. " +
                    "(Alternatively set WATCHTOWER__AUTH__BOOTSTRAPPASSWORD before restarting.)");
        }

        await SetUnlessPinnedAsync(WatchtowerSettingPaths.AuthEnabled, command.Enabled ? "true" : "false", ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.AuthHost, host, ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.AuthSessionLifetimeHours, command.SessionLifetimeHours.ToString(), ct);
        await SetUnlessPinnedAsync(WatchtowerSettingPaths.AuthAbsoluteSessionLifetimeDays, command.AbsoluteSessionLifetimeDays.ToString(), ct);

        // Recorded post-write with the new effective values — turning auth on or off is exactly the
        // kind of change the trail exists for.
        await audit.RecordAsync("system", "auth.config.update", "auth settings",
            $"auth {(command.Enabled ? "on" : "off")}"
            + (host.Length > 0 ? $" · host {host}" : "")
            + $" · session {command.SessionLifetimeHours}h / absolute {command.AbsoluteSessionLifetimeDays}d"
            + (command.Enabled != startup.Enabled ? " · restart required" : ""),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        // Echo the written values (the config provider reloads asynchronously — same reasoning as
        // system.updateAutomation): immediately consistent for the caller.
        return new Response(
            Enabled: command.Enabled,
            Active: startup.Enabled,
            RestartRequired: command.Enabled != startup.Enabled,
            Host: host.Length > 0 ? host : null,
            SessionLifetimeHours: command.SessionLifetimeHours,
            AbsoluteSessionLifetimeDays: command.AbsoluteSessionLifetimeDays,
            PinnedPaths: pins.Pinned(GetAuthConfig.AuthPaths));
    }

    private Task SetUnlessPinnedAsync(string path, string value, CancellationToken ct) =>
        pins.IsPinned(path)
            ? Task.CompletedTask
            : settings.SetStringAsync(path, value, SettingsScope.Global, expectedVersion: null, ct).AsTask();
}
