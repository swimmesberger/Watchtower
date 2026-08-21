using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Modules.System.Handlers;
using Watchtower.Application.Persistence;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the audit rows the settings surfaces write on every successful save (category per plane,
/// action <c>*.update</c>): the automation toggles and the auth configuration — the latter being
/// the change the trail most exists for. The metrics, proxy and backup surfaces follow the same
/// post-write pattern (see <see cref="BackupAuditTests"/> for the secrets-never-recorded contract).
/// </summary>
public sealed class ConfigAuditTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpdateAutomation_RecordsTheEffectiveToggles() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateAutomation>(scope.ServiceProvider);

        var result = await handler.HandleAsync(new UpdateAutomation.Command(
            AutoCheckEnabled: true, AutoCheckIntervalMinutes: 30,
            StackCheckEnabled: false, StackCheckIntervalMinutes: 15,
            ImagePruneEnabled: true, ImagePruneIntervalMinutes: 1440), Ct);
        Assert.True(result.IsSuccess);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.SingleAsync(Ct);
        Assert.Equal("system", row.Category);
        Assert.Equal("automation.update", row.Action);
        Assert.True(row.Success);
        Assert.Equal("self-update check on (30m) · stack check off · image prune on (1440m)", row.Detail);
    }

    [Fact]
    public async Task UpdateAuthConfig_RecordsTheChange() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateAuthConfig>(scope.ServiceProvider);

        // Disabling needs no admin account to exist, so it exercises the write path directly.
        var result = await handler.HandleAsync(new UpdateAuthConfig.Command(
            Enabled: false, Host: "watchtower.example.com",
            SessionLifetimeHours: 24, AbsoluteSessionLifetimeDays: 30), Ct);
        Assert.True(result.IsSuccess);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.SingleAsync(Ct);
        Assert.Equal("system", row.Category);
        Assert.Equal("auth.config.update", row.Action);
        Assert.Contains("auth off", row.Detail);
        Assert.Contains("host watchtower.example.com", row.Detail);
        Assert.Contains("session 24h / absolute 30d", row.Detail);
    }
}
