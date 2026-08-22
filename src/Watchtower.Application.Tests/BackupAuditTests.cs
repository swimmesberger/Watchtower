using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Modules.Backups.Handlers;
using Watchtower.Application.Persistence;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the backup plane's audit rows (category <c>backups</c>): a configuration change records
/// the effective values with secrets reduced to which fields were touched, and a failed storage
/// test records the failure. The run/restore/prune rows share the same recorder and are exercised
/// by their services against a live daemon, not here.
/// </summary>
public sealed class BackupAuditTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UpdateConfig_RecordsTheEffectiveValues_NeverTheSecrets() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateBackupConfig>(scope.ServiceProvider);

        var result = await handler.HandleAsync(new UpdateBackupConfig.Command(
            Enabled: true, Cron: "30 3,15 * * *", InstanceName: "test-instance",
            RetentionDays: 14, RetentionMaxCount: 5,
            HelperImage: "busybox:stable", Provider: "sftp",
            EncryptionPassphrase: "s3cret-passphrase", SftpPassword: "hunter2"), Ct);
        Assert.True(result.IsSuccess);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.SingleAsync(Ct);
        Assert.Equal("backups", row.Category);
        Assert.Equal("config.update", row.Action);
        Assert.True(row.Success);
        Assert.Contains("schedule on, 30 3,15 * * * (every day at 03:30 and 15:30)", row.Detail);
        Assert.Contains("provider sftp", row.Detail);
        Assert.Contains("retention 14d, keep 5", row.Detail);
        Assert.Contains("encrypted", row.Detail);
        // Secrets appear only as the names of the fields that changed — never their values.
        Assert.Contains("secrets updated: encryption passphrase, SFTP password", row.Detail);
        Assert.DoesNotContain("s3cret-passphrase", row.Detail);
        Assert.DoesNotContain("hunter2", row.Detail);
    }

    [Fact]
    public async Task StorageTest_Failure_IsRecorded() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<TestBackupStorage>(scope.ServiceProvider);

        // The default configuration is the sftp provider with no host — the probe must fail.
        var result = await handler.HandleAsync(new TestBackupStorage.Command(), Ct);
        Assert.False(result.IsSuccess);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.SingleAsync(Ct);
        Assert.Equal("backups", row.Category);
        Assert.Equal("storage.test", row.Action);
        Assert.Equal("sftp", row.Target);
        Assert.False(row.Success);
        Assert.Contains("Host is empty", row.Error);
    }
}
