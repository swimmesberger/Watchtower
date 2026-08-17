using Elarion.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.System.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the runtime-editable auth settings (<c>system.getAuthConfig</c> / <c>system.updateAuthConfig</c>):
/// the restart-required contract around <c>Auth:Enabled</c> (which shapes the pipeline pre-DI and cannot
/// switch live), the lockout guard that refuses to enable auth with no admin to log in as, and the
/// env-pin rejection that keeps the env-wins layering honest.
/// </summary>
public sealed class AuthConfigSettingsTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EnablingWithoutAnAdminAccount_IsRefused() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateAuthConfig>(scope.ServiceProvider);

        var result = await handler.HandleAsync(
            new UpdateAuthConfig.Command(Enabled: true, Host: null, SessionLifetimeHours: 12, AbsoluteSessionLifetimeDays: 7),
            Ct);

        Assert.False(result.IsSuccess);
        Assert.Contains("admin", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        // Nothing was persisted — a refused enable must not half-apply.
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        Assert.Null(await settings.GetStringAsync(WatchtowerSettingPaths.AuthEnabled, SettingsScope.Global, Ct));
    }

    [Fact]
    public async Task EnablingWithAnAdmin_PersistsAndReportsRestartRequired() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var admin = AuthTestHost.NewUser("admin");
        admin.IsAdmin = true;
        admin.PasswordHash = "not-a-real-hash";
        db.Users.Add(admin);
        await db.SaveChangesAsync(Ct);

        var handler = ActivatorUtilities.CreateInstance<UpdateAuthConfig>(scope.ServiceProvider);
        var result = await handler.HandleAsync(
            new UpdateAuthConfig.Command(
                Enabled: true, Host: "Watchtower.Example.COM", SessionLifetimeHours: 24, AbsoluteSessionLifetimeDays: 14),
            Ct);

        Assert.True(result.IsSuccess);
        // The process started with auth off, so the stored enable needs a restart to shape the pipeline.
        Assert.True(result.Value.RestartRequired);
        Assert.False(result.Value.Active);
        Assert.Equal("watchtower.example.com", result.Value.Host);

        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        Assert.Equal("true", await settings.GetStringAsync(WatchtowerSettingPaths.AuthEnabled, SettingsScope.Global, Ct));
        Assert.Equal("watchtower.example.com", await settings.GetStringAsync(WatchtowerSettingPaths.AuthHost, SettingsScope.Global, Ct));
        Assert.Equal("24", await settings.GetStringAsync(WatchtowerSettingPaths.AuthSessionLifetimeHours, SettingsScope.Global, Ct));
    }

    [Fact]
    public async Task DisablingWhileActive_ReportsRestartRequired() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Enabled", "true"));
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateAuthConfig>(scope.ServiceProvider);

        var result = await handler.HandleAsync(
            new UpdateAuthConfig.Command(Enabled: false, Host: null, SessionLifetimeHours: 12, AbsoluteSessionLifetimeDays: 7),
            Ct);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.RestartRequired);
        Assert.True(result.Value.Active);
    }

    [Fact]
    public async Task GetAuthConfig_AgreesWithTheStartupSnapshot() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<GetAuthConfig>(scope.ServiceProvider);

        var result = await handler.HandleAsync(new GetAuthConfig.Query(), Ct);

        Assert.True(result.IsSuccess);
        Assert.False(result.Value.Enabled);
        Assert.False(result.Value.Active);
        Assert.False(result.Value.RestartRequired);
        Assert.Empty(result.Value.PinnedPaths);
    }

    [Fact]
    public async Task ChangingAnEnvPinnedToggle_IsRejectedAndNamesTheVariable() {
        using var host = AuthTestHost.Start(
            configure: services => {
                services.RemoveAll<EnvironmentSettingPins>();
                services.AddSingleton(new EnvironmentSettingPins(["WATCHTOWER__AUTH__ENABLED"]));
            },
            // Simulates the pinned env value being part of effective configuration.
            ("Watchtower:Auth:Enabled", "false"));
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateAuthConfig>(scope.ServiceProvider);

        var result = await handler.HandleAsync(
            new UpdateAuthConfig.Command(Enabled: true, Host: null, SessionLifetimeHours: 12, AbsoluteSessionLifetimeDays: 7),
            Ct);

        Assert.False(result.IsSuccess);
        Assert.Contains("WATCHTOWER__AUTH__ENABLED", result.Error.Message);
    }

    [Fact]
    public async Task UnchangedPinnedValues_AreAcceptedButNeverWritten() {
        using var host = AuthTestHost.Start(
            configure: services => {
                services.RemoveAll<EnvironmentSettingPins>();
                services.AddSingleton(new EnvironmentSettingPins(["WATCHTOWER__AUTH__ENABLED"]));
            });
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateAuthConfig>(scope.ServiceProvider);

        // Enabled stays false (the pinned/effective value) — only the lifetimes change.
        var result = await handler.HandleAsync(
            new UpdateAuthConfig.Command(Enabled: false, Host: null, SessionLifetimeHours: 48, AbsoluteSessionLifetimeDays: 30),
            Ct);

        Assert.True(result.IsSuccess);
        Assert.Equal([WatchtowerSettingPaths.AuthEnabled], result.Value.PinnedPaths);

        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        // The pinned path was skipped — a stored row shadowed by env would be a lie waiting for the
        // variable's removal — while the editable paths persisted normally.
        Assert.Null(await settings.GetStringAsync(WatchtowerSettingPaths.AuthEnabled, SettingsScope.Global, Ct));
        Assert.Equal("48", await settings.GetStringAsync(WatchtowerSettingPaths.AuthSessionLifetimeHours, SettingsScope.Global, Ct));
    }
}
