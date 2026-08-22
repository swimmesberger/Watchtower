using Elarion.Abstractions;
using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Backups.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The schedule half of the backup configuration handlers (ADR-0018): expressions are validated
/// with an operator-readable message, the instance schedule replaces the legacy stored alias, the
/// legacy env var pins the schedule field, and a stack's override round-trips and is audited.
/// </summary>
public sealed class BackupScheduleConfigTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UpdateBackupConfig.Command Update(string cron) => new(
        Enabled: true, Cron: cron, InstanceName: null, RetentionDays: 30, RetentionMaxCount: 0,
        HelperImage: "busybox:stable", Provider: "sftp");

    [Theory]
    [InlineData("03:30", "exactly five fields")]
    [InlineData("30 25 * * *", "not a valid cron expression")]
    [InlineData("", "five fields")]
    public async Task UpdateConfig_RejectsAnInvalidSchedule(string cron, string expectedFragment) {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateBackupConfig>(scope.ServiceProvider);

        var result = await handler.HandleAsync(Update(cron), Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains(expectedFragment, result.Error.Message);
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Empty(await db.AuditEvents.ToListAsync(Ct));
    }

    [Fact]
    public async Task UpdateConfig_StoresTheCronAndRetiresTheStoredLegacyAlias() {
        // An upgraded instance: the old UI stored Backup:Time, which is what the schedule runs on today.
        using var host = AuthTestHost.Start(("Watchtower:Backup:Time", "04:00"));
        await using var scope = host.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        await settings.SetStringAsync(WatchtowerSettingPaths.BackupTime, "04:00", SettingsScope.Global, expectedVersion: null, Ct);

        var read = ActivatorUtilities.CreateInstance<GetBackupConfig>(scope.ServiceProvider);
        var before = await read.HandleAsync(new GetBackupConfig.Query(), Ct);
        Assert.Equal("0 4 * * *", before.Value.Config.Cron);

        var update = ActivatorUtilities.CreateInstance<UpdateBackupConfig>(scope.ServiceProvider);
        var result = await update.HandleAsync(Update("30 3,15 * * *"), Ct);
        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Equal("30 3,15 * * *", result.Value.Config.Cron);

        Assert.Equal("30 3,15 * * *", await settings.GetStringAsync(WatchtowerSettingPaths.BackupCron, SettingsScope.Global, Ct));
        Assert.Null(await settings.GetStringAsync(WatchtowerSettingPaths.BackupTime, SettingsScope.Global, Ct));
    }

    [Fact]
    public async Task UpdateConfig_AnUnchangedScheduleLeavesTheStoreAlone() {
        using var host = AuthTestHost.Start(("Watchtower:Backup:Time", "04:00"));
        await using var scope = host.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        var update = ActivatorUtilities.CreateInstance<UpdateBackupConfig>(scope.ServiceProvider);

        var result = await update.HandleAsync(Update("0 4 * * *"), Ct);

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Null(await settings.GetStringAsync(WatchtowerSettingPaths.BackupCron, SettingsScope.Global, Ct));
    }

    [Fact]
    public async Task TheLegacyTimeEnvVarPinsTheScheduleField() {
        using var host = AuthTestHost.Start(
            services => services.Replace(ServiceDescriptor.Singleton(new EnvironmentSettingPins(["WATCHTOWER__BACKUP__TIME"]))),
            ("Watchtower:Backup:Time", "04:00"));
        await using var scope = host.Services.CreateAsyncScope();

        var read = ActivatorUtilities.CreateInstance<GetBackupConfig>(scope.ServiceProvider);
        var config = (await read.HandleAsync(new GetBackupConfig.Query(), Ct)).Value.Config;
        Assert.Equal("0 4 * * *", config.Cron);
        Assert.Contains(WatchtowerSettingPaths.BackupCron, config.PinnedPaths);
        Assert.Contains(WatchtowerSettingPaths.BackupTime, config.PinnedPaths);

        var update = ActivatorUtilities.CreateInstance<UpdateBackupConfig>(scope.ServiceProvider);
        var rejected = await update.HandleAsync(Update("30 3,15 * * *"), Ct);
        Assert.False(rejected.IsSuccess);
        Assert.Contains("WATCHTOWER__BACKUP__TIME", rejected.Error.Message);

        // Leaving the schedule as it is still saves the other fields.
        var accepted = await update.HandleAsync(Update("0 4 * * *"), Ct);
        Assert.True(accepted.IsSuccess, accepted.IsSuccess ? null : accepted.Error.Message);
    }

    [Fact]
    public async Task StackConfig_OverrideRoundTripsAndIsAudited() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = "web-app", RepositoryUrl = "https://example.com/web-app.git", ComposeFilePath = "docker-compose.yml",
            Branch = "main", ComposeProjectName = "web-app",
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);

        var set = ActivatorUtilities.CreateInstance<SetStackBackupConfig>(scope.ServiceProvider);
        var result = await set.HandleAsync(new SetStackBackupConfig.Command(stack.Id, Enabled: true, StopContainers: true, Cron: " 0 */6 * * * "), Ct);
        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Equal("0 */6 * * *", result.Value.Config.Cron);

        var get = ActivatorUtilities.CreateInstance<GetStackBackupConfig>(scope.ServiceProvider);
        Assert.Equal("0 */6 * * *", (await get.HandleAsync(new GetStackBackupConfig.Query(stack.Id), Ct)).Value.Config.Cron);

        var row = await db.AuditEvents.OrderBy(e => e.Id).LastAsync(Ct);
        Assert.Equal("backups", row.Category);
        Assert.Equal("stack.config.update", row.Action);
        Assert.Equal("web-app", row.Target);
        Assert.Contains("backups on", row.Detail);
        Assert.Contains("schedule 0 */6 * * * (every 6 hours at :00)", row.Detail);

        // Blank clears the override — back to the instance schedule.
        var cleared = await set.HandleAsync(new SetStackBackupConfig.Command(stack.Id, Enabled: true, StopContainers: false, Cron: "  "), Ct);
        Assert.True(cleared.IsSuccess);
        Assert.Null(cleared.Value.Config.Cron);
        row = await db.AuditEvents.OrderBy(e => e.Id).LastAsync(Ct);
        Assert.Contains("schedule: instance default", row.Detail);
        Assert.Contains("keep containers running", row.Detail);
    }

    [Fact]
    public async Task StackConfig_RejectsAnInvalidOverrideWithoutTouchingTheStack() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = "web-app", RepositoryUrl = "https://example.com/web-app.git", ComposeFilePath = "docker-compose.yml",
            Branch = "main", ComposeProjectName = "web-app", BackupEnabled = false,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);

        var set = ActivatorUtilities.CreateInstance<SetStackBackupConfig>(scope.ServiceProvider);
        var result = await set.HandleAsync(new SetStackBackupConfig.Command(stack.Id, Enabled: true, StopContainers: true, Cron: "every day"), Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains("exactly five fields", result.Error.Message);
        var reloaded = await db.Stacks.AsNoTracking().SingleAsync(s => s.Id == stack.Id, Ct);
        Assert.False(reloaded.BackupEnabled);
        Assert.Null(reloaded.BackupCron);
        Assert.Empty(await db.AuditEvents.ToListAsync(Ct));
    }
}
