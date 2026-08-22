using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The backup file-name format and the retention selection built on it (ADR-0016 §3/§5). Retention
/// works from names alone, so the format is a contract: it must round-trip, foreign files must never
/// match, and the newest backup must never be selected for deletion.
/// </summary>
public sealed class BackupNamingAndRetentionTests {
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    private static string At(int daysAgo) =>
        BackupNaming.FileName("web-app", Now.AddDays(-daysAgo), encrypted: false);

    // ── Naming ───────────────────────────────────────────────────────────────

    [Fact]
    public void FileNamesRoundTripTheirTimestamp() {
        var taken = new DateTimeOffset(2026, 8, 17, 3, 30, 15, TimeSpan.Zero);
        var plain = BackupNaming.FileName("web-app", taken, encrypted: false);
        var encrypted = BackupNaming.FileName("web-app", taken, encrypted: true);

        Assert.Equal("web-app_20260817T033015Z.tar.gz", plain);
        Assert.Equal("web-app_20260817T033015Z.tar.gz.enc", encrypted);
        Assert.Equal(taken, BackupNaming.ParseTimestamp(plain));
        Assert.Equal(taken, BackupNaming.ParseTimestamp(encrypted));
    }

    [Fact]
    public void TheTimestampIsAlwaysUtc() {
        // A local-offset input must land as its UTC instant, not its wall-clock digits.
        var local = new DateTimeOffset(2026, 8, 17, 3, 30, 0, TimeSpan.FromHours(2));
        var name = BackupNaming.FileName("app", local, encrypted: false);
        Assert.Equal("app_20260817T013000Z.tar.gz", name);
    }

    [Theory]
    [InlineData("random-file.txt")]
    [InlineData("web-app.tar.gz")]                       // no timestamp
    [InlineData("web-app_20260817T033015Z.tar.gz.bak")]  // wrong suffix
    [InlineData("web-app_2026081T033015Z.tar.gz")]       // malformed timestamp
    public void ForeignFileNamesDoNotParse(string name) =>
        Assert.Null(BackupNaming.ParseTimestamp(name));

    [Theory]
    [InlineData("web app/2", "web-app-2")]
    [InlineData("..", "unnamed")]
    [InlineData("  ", "unnamed")]
    [InlineData("Stack.Name-1_x", "Stack.Name-1_x")]
    public void SanitizeYieldsASafeSingleSegment(string input, string expected) =>
        Assert.Equal(expected, BackupNaming.Sanitize(input));

    // ── Retention ────────────────────────────────────────────────────────────

    [Fact]
    public void AgeLimitDeletesOnlyBackupsPastTheCutoff() {
        var files = new[] { At(0), At(5), At(31), At(40) };
        var deleted = BackupRetention.SelectDeletions(files, Now, retentionDays: 30, retentionMaxCount: 0);
        Assert.Equal([At(31), At(40)], deleted);
    }

    [Fact]
    public void CountLimitKeepsTheNewestN() {
        var files = new[] { At(3), At(0), At(2), At(1) };
        var deleted = BackupRetention.SelectDeletions(files, Now, retentionDays: 0, retentionMaxCount: 2);
        Assert.Equal([At(2), At(3)], deleted);
    }

    [Fact]
    public void TheNewestBackupSurvivesEvenWhenOlderThanTheAgeLimit() {
        // Backups stopped for a while — everything is past the cutoff. The newest must survive.
        var files = new[] { At(90), At(100) };
        var deleted = BackupRetention.SelectDeletions(files, Now, retentionDays: 30, retentionMaxCount: 1);
        Assert.Equal([At(100)], deleted);
    }

    [Fact]
    public void ForeignFilesAreNeverSelected() {
        var files = new[] { "notes.txt", At(500), At(0) };
        var deleted = BackupRetention.SelectDeletions(files, Now, retentionDays: 1, retentionMaxCount: 1);
        Assert.Equal([At(500)], deleted);
    }

    [Fact]
    public void ZeroLimitsDeleteNothing() {
        var files = new[] { At(0), At(1000) };
        Assert.Empty(BackupRetention.SelectDeletions(files, Now, retentionDays: 0, retentionMaxCount: 0));
    }

    // ── Audit summary ────────────────────────────────────────────────────────

    private static Stack StackWithStops() => new() {
        Name = "web-app",
        RepositoryUrl = "https://example.com/web-app.git",
        ComposeFilePath = "docker-compose.yml",
        Branch = "main",
        ComposeProjectName = "web-app",
        BackupStopContainers = true,
    };

    [Fact]
    public void TheAuditSummaryReportsWhatTheRunActuallyStoppedAndExcluded() {
        var backup = new BackupOptions { Provider = "local", RetentionDays = 30, RetentionMaxCount = 0 };

        var summary = BackupService.RunSummary(
            "manual", StackWithStops(), backup, stoppedCount: 2, excludedVolumeCount: 1);

        Assert.Equal("manual · local · 2 container(s) stopped · 1 volume(s) excluded · retention 30d", summary);
    }

    [Fact]
    public void AFailedRunReportsTheSettingRatherThanACountItCannotVouchFor() {
        var stack = StackWithStops();
        var backup = new BackupOptions { Provider = "local", RetentionDays = 0, RetentionMaxCount = 0 };

        // No count: the run may have failed before it reached its stop step.
        Assert.Equal("manual · local · containers stopped · keep forever",
            BackupService.RunSummary("manual", stack, backup));
        // Mount scoping can legitimately stop nothing at all — that is not "containers stopped".
        Assert.Equal("manual · local · keep forever",
            BackupService.RunSummary("manual", stack, backup, stoppedCount: 0));
    }
}
