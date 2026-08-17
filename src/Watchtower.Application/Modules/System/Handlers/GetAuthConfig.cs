using Elarion.Abstractions.Authorization;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Returns the auth configuration for the Settings page: the configured values (which may already be
/// runtime-edited through the settings store), whether the auth pipeline is actually active in this
/// process, and the restart-required flag when the two disagree — <c>Auth:Enabled</c> shapes the
/// pipeline before DI exists (<c>Program.cs</c>) and cannot switch without a restart. Env-pinned paths
/// ride along so the UI disables those fields (env wins over the settings store).
/// </summary>
[Handler("system.getAuthConfig")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class GetAuthConfig(
    IOptionsMonitor<WatchtowerOptions> options,
    AuthStartupState startup,
    EnvironmentSettingPins pins)
    : IHandler<GetAuthConfig.Query, Result<GetAuthConfig.Response>> {
    public sealed record Query;

    /// <param name="Enabled">The configured value (what the next start will run with).</param>
    /// <param name="Active">Whether the auth pipeline is enforcing in this process right now.</param>
    /// <param name="RestartRequired">True when <paramref name="Enabled"/> ≠ <paramref name="Active"/>.</param>
    public sealed record Response(
        bool Enabled,
        bool Active,
        bool RestartRequired,
        string? Host,
        int SessionLifetimeHours,
        int AbsoluteSessionLifetimeDays,
        string[] PinnedPaths);

    /// <summary>Every path the auth card manages — shared with <see cref="UpdateAuthConfig"/>.</summary>
    internal static readonly string[] AuthPaths = [
        WatchtowerSettingPaths.AuthEnabled,
        WatchtowerSettingPaths.AuthHost,
        WatchtowerSettingPaths.AuthSessionLifetimeHours,
        WatchtowerSettingPaths.AuthAbsoluteSessionLifetimeDays,
    ];

    public ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var auth = options.CurrentValue.Auth;
        var response = new Response(
            Enabled: auth.Enabled,
            Active: startup.Enabled,
            RestartRequired: auth.Enabled != startup.Enabled,
            Host: auth.Host,
            SessionLifetimeHours: auth.SessionLifetimeHours,
            AbsoluteSessionLifetimeDays: auth.AbsoluteSessionLifetimeDays,
            PinnedPaths: pins.Pinned(AuthPaths));
        return ValueTask.FromResult<Result<Response>>(response);
    }
}
