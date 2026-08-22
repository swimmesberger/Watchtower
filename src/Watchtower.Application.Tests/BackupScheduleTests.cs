using Elarion.Abstractions.Scheduling;
using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The pure schedule logic (ADR-0018): how the instance-wide expression is resolved (cron, legacy
/// <c>HH:mm</c> alias, default), what the validator accepts, how expressions read in words, and —
/// the part a restart must get right — which window <see cref="BackupSchedule.Evaluate"/> fires,
/// skips, or leaves alone given the stack's cursor and the misfire grace.
/// </summary>
public sealed class BackupScheduleTests {
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(60);

    private static DateTimeOffset Utc(int day, int hour, int minute, int second = 0) =>
        new(2026, 8, day, hour, minute, second, TimeSpan.Zero);

    private static CronExpression Cron(string expression) {
        Assert.True(BackupSchedule.TryParse(expression, out var cron, out var error), error);
        return cron;
    }

    // ── Resolution: Cron, the HH:mm alias, the default ──────────────────────

    [Fact]
    public void ExplicitCronWinsOverTheLegacyAlias() =>
        Assert.Equal("0 */6 * * *", BackupSchedule.ResolveGlobalExpression(new BackupOptions { Cron = " 0 */6 * * * ", Time = "03:30" }));

    [Theory]
    [InlineData("03:30", "30 3 * * *")]
    [InlineData("00:00", "0 0 * * *")]
    [InlineData("23:59", "59 23 * * *")]
    [InlineData(" 15:30 ", "30 15 * * *")]
    public void TheLegacyTimeSettingReadsAsADailyCron(string time, string expected) =>
        Assert.Equal(expected, BackupSchedule.ResolveGlobalExpression(new BackupOptions { Time = time }));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3:30pm")]
    [InlineData("25:00")]
    public void WithoutAUsableSettingTheDefaultApplies(string? time) =>
        Assert.Equal(BackupSchedule.DefaultExpression, BackupSchedule.ResolveGlobalExpression(new BackupOptions { Time = time }));

    [Fact]
    public void TheDefaultIsTheOldThreeThirty() =>
        Assert.Equal("30 3 * * *", BackupSchedule.DefaultExpression);

    [Theory]
    [InlineData(null, "30 3 * * *", "30 3 * * *")]
    [InlineData("  ", "30 3 * * *", "30 3 * * *")]
    [InlineData(" 0 */6 * * * ", "30 3 * * *", "0 */6 * * *")]
    public void AStackOverrideReplacesTheInstanceExpression(string? stackCron, string global, string expected) =>
        Assert.Equal(expected, BackupSchedule.Effective(stackCron, global));

    [Theory]
    [InlineData(0, 2)]      // floor: two ticks
    [InlineData(1, 2)]
    [InlineData(60, 60)]
    [InlineData(100_000, 1440)] // ceiling: a day
    public void TheMisfireGraceIsClamped(int minutes, int expectedMinutes) =>
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes),
            BackupSchedule.ResolveMisfireGrace(new BackupOptions { MisfireGraceMinutes = minutes }));

    // ── Validation ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("30 3 * * *")]
    [InlineData("30 3,15 * * *")]
    [InlineData("0 */6 * * *")]
    [InlineData("0 2 * * 1-5")]
    [InlineData("0 3 1,15 * *")]
    [InlineData("0 3 * * MON,WED,FRI")]
    [InlineData("  15 4 * JAN-JUN *  ")]
    public void ValidExpressionsParse(string expression) {
        Assert.True(BackupSchedule.TryParse(expression, out var cron, out var error), error);
        Assert.NotNull(cron);
    }

    [Theory]
    [InlineData("", "five fields")]
    [InlineData("   ", "five fields")]
    [InlineData("03:30", "exactly five fields")]
    [InlineData("30 3 * *", "exactly five fields")]
    [InlineData("0 30 3 * * *", "exactly five fields")]          // six fields (seconds) are not a thing here
    [InlineData("30 25 * * *", "not a valid cron expression")]
    [InlineData("60 3 * * *", "not a valid cron expression")]
    [InlineData("30 3 * * MONDAY", "not a valid cron expression")]
    [InlineData("0 0 31 2 *", "never occurs")]
    public void InvalidExpressionsAreRejectedWithAnOperatorMessage(string expression, string expectedFragment) {
        Assert.False(BackupSchedule.TryParse(expression, out _, out var error));
        Assert.Contains(expectedFragment, error);
    }

    // ── Description ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("30 3 * * *", "every day at 03:30")]
    [InlineData("30 3,15 * * *", "every day at 03:30 and 15:30")]
    [InlineData("0 1,9,17 * * *", "every day at 01:00, 09:00 and 17:00")]
    [InlineData("0 */6 * * *", "every 6 hours at :00")]
    [InlineData("30 0-23/2 * * *", "every 2 hours at :30")]
    [InlineData("15 * * * *", "every hour at :15")]
    [InlineData("*/5 * * * *", "every 5 minutes")]
    [InlineData("* * * * *", "every minute")]
    [InlineData("0 2 * * 1-5", "on weekdays at 02:00")]
    [InlineData("0 2 * * 0,6", "on weekends at 02:00")]
    [InlineData("0 3 * * MON,WED,FRI", "on Mon, Wed and Fri at 03:00")]
    [InlineData("0 3 * * 7", "on Sun at 03:00")]
    [InlineData("0 3 * * 1-5,0,6", "every day at 03:00")]
    [InlineData("0 3 1,15 * *", "on day 1 and 15 of every month at 03:00")]
    [InlineData("0 */6 * * 1-5", "on weekdays every 6 hours at :00")]
    [InlineData("0 0,3,6,9,12,15,18,21,23 * * *", "9 times a day")]
    [InlineData("0 3 1 1 *", "cron \"0 3 1 1 *\"")]           // month restricted — not described
    [InlineData("0 3 1 * 1", "cron \"0 3 1 * 1\"")]           // both day fields restricted (OR semantics)
    [InlineData("* 3 * * *", "cron \"* 3 * * *\"")]           // every minute of one hour — not described
    [InlineData("not a cron", "cron \"not a cron\"")]
    public void ExpressionsReadInWords(string expression, string expected) =>
        Assert.Equal(expected, BackupSchedule.Describe(expression));

    // ── Evaluate: the tick's decision ───────────────────────────────────────

    [Fact]
    public void AWindowThatJustOpenedFires() {
        var decision = BackupSchedule.Evaluate(Cron("30 3 * * *"), Utc(17, 3, 30, 20), Utc(16, 3, 30), Grace, TimeZoneInfo.Utc);
        Assert.Equal(Utc(17, 3, 30), decision.DueAt);
        Assert.Null(decision.MissedAt);
    }

    [Fact]
    public void AWindowNeverFiresTwice() {
        // The tick a minute later — and every tick after a restart — sees the cursor at today's window.
        var decision = BackupSchedule.Evaluate(Cron("30 3 * * *"), Utc(17, 3, 31, 5), Utc(17, 3, 30), Grace, TimeZoneInfo.Utc);
        Assert.Null(decision.DueAt);
        Assert.Null(decision.MissedAt);
    }

    [Fact]
    public void BeforeTheWindowNothingIsDue() {
        var decision = BackupSchedule.Evaluate(Cron("30 3 * * *"), Utc(17, 3, 29, 59), Utc(16, 3, 30), Grace, TimeZoneInfo.Utc);
        Assert.Null(decision.DueAt);
        Assert.Null(decision.MissedAt);
    }

    [Fact]
    public void TwoWindowsADayEachFireOnce() {
        var cron = Cron("30 3,15 * * *");
        var morning = BackupSchedule.Evaluate(cron, Utc(17, 3, 30, 10), Utc(16, 15, 30), Grace, TimeZoneInfo.Utc);
        Assert.Equal(Utc(17, 3, 30), morning.DueAt);
        var noon = BackupSchedule.Evaluate(cron, Utc(17, 12, 0), morning.DueAt, Grace, TimeZoneInfo.Utc);
        Assert.Null(noon.DueAt);
        var afternoon = BackupSchedule.Evaluate(cron, Utc(17, 15, 30, 10), morning.DueAt, Grace, TimeZoneInfo.Utc);
        Assert.Equal(Utc(17, 15, 30), afternoon.DueAt);
        Assert.Null(afternoon.MissedAt);
    }

    [Fact]
    public void AWindowMissedWhileDownRunsOnceWithinTheGrace() {
        // Watchtower was down 03:00–04:10; the 03:30 window is 40 minutes old when the first tick runs.
        var decision = BackupSchedule.Evaluate(Cron("30 3 * * *"), Utc(17, 4, 10), Utc(16, 3, 30), Grace, TimeZoneInfo.Utc);
        Assert.Equal(Utc(17, 3, 30), decision.DueAt);
        Assert.Null(decision.MissedAt);
    }

    [Fact]
    public void AWindowMissedByMoreThanTheGraceIsSkippedAndReported() {
        var decision = BackupSchedule.Evaluate(Cron("30 3 * * *"), Utc(17, 6, 0), Utc(16, 3, 30), Grace, TimeZoneInfo.Utc);
        Assert.Null(decision.DueAt);
        Assert.Equal(Utc(17, 3, 30), decision.MissedAt);
    }

    [Fact]
    public void AfterALongOutageOnlyTheLatestWindowRunsNeverABurst() {
        // Every 6 hours; down since just after midnight, back at 18:30. 06:00 and 12:00 are gone,
        // 18:00 is 30 minutes old → runs once. The first missed window is what the log names.
        var decision = BackupSchedule.Evaluate(Cron("0 */6 * * *"), Utc(17, 18, 30), Utc(17, 0, 0), Grace, TimeZoneInfo.Utc);
        Assert.Equal(Utc(17, 18, 0), decision.DueAt);
        Assert.Equal(Utc(17, 6, 0), decision.MissedAt);
    }

    [Fact]
    public void WithoutHistoryARecentWindowRunsAndAnOldOneDoesNot() {
        // A stack opted in (or an upgraded instance with no scheduled runs yet): same grace rule, and
        // there is nothing to call "missed" because there is nothing to compare against.
        var cron = Cron("30 3 * * *");
        var recent = BackupSchedule.Evaluate(cron, Utc(17, 3, 40), lastScheduledAt: null, Grace, TimeZoneInfo.Utc);
        Assert.Equal(Utc(17, 3, 30), recent.DueAt);
        Assert.Null(recent.MissedAt);
        var old = BackupSchedule.Evaluate(cron, Utc(17, 5, 0), lastScheduledAt: null, Grace, TimeZoneInfo.Utc);
        Assert.Null(old.DueAt);
        Assert.Null(old.MissedAt);
    }

    [Fact]
    public void TheGraceFloorStillCoversTheTickItself() {
        // Grace 0 clamps to two minutes: a window seen 90 seconds late (one tick plus jitter) still runs;
        // three minutes late it is gone.
        var cron = Cron("30 3 * * *");
        Assert.Equal(Utc(17, 3, 30), BackupSchedule.Evaluate(cron, Utc(17, 3, 31, 30), null, TimeSpan.Zero, TimeZoneInfo.Utc).DueAt);
        Assert.Null(BackupSchedule.Evaluate(cron, Utc(17, 3, 33), null, TimeSpan.Zero, TimeZoneInfo.Utc).DueAt);
    }

    [Fact]
    public void ACursorAheadOfTheWindowNeverFires() {
        // The schedule moved from 15:30 to 03:30 while today's 15:30 already ran; tomorrow's 03:30 is the
        // next thing to happen, never today's (older) 03:30.
        var decision = BackupSchedule.Evaluate(Cron("30 3 * * *"), Utc(17, 15, 40), Utc(17, 15, 30), TimeSpan.FromHours(24), TimeZoneInfo.Utc);
        Assert.Null(decision.DueAt);
    }

    [Fact]
    public void ExpressionsAreWallClockTimeInTheGivenZone() {
        // Vienna is UTC+2 in August: "03:30" is 01:30Z. The window opens at 01:30Z, not at 03:30Z.
        var vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");
        var cron = Cron("30 3 * * *");
        var tooEarly = BackupSchedule.Evaluate(cron, Utc(17, 1, 29, 50), Utc(16, 1, 30), Grace, vienna);
        Assert.Null(tooEarly.DueAt);
        var open = BackupSchedule.Evaluate(cron, Utc(17, 1, 30, 10), Utc(16, 1, 30), Grace, vienna);
        Assert.Equal(Utc(17, 1, 30), open.DueAt);
        var utcWallClock = BackupSchedule.Evaluate(cron, Utc(17, 3, 30, 10), Utc(17, 1, 30), Grace, vienna);
        Assert.Null(utcWallClock.DueAt);
    }
}
