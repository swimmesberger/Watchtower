using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers how a completed check folds into the runtime record — in particular what happens to a
/// lingering "error" apply stage. A user-initiated check clears it (otherwise the failure banner
/// has no way to go away short of a successful update), while a background check leaves it alone
/// (its next tick would wipe the banner before the user had seen it).
/// </summary>
public sealed class SelfUpdateCheckAcknowledgeTests {
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly SelfUpdateRuntime FailedApply = new() {
        ApplyStage = "error",
        ApplyError = "Response status code does not indicate success: 500 (Internal Server Error).",
    };

    [Fact]
    public void AManualCheckClearsALingeringApplyError() {
        var runtime = SelfUpdateService.ApplyCheckResult(
            FailedApply, "sha256:aaa", "sha256:bbb", isOutdated: true,
            acknowledgeApplyError: true, checkedAt: Now);

        Assert.Equal("idle", runtime.ApplyStage);
        Assert.Null(runtime.ApplyError);
        // The check result itself still lands.
        Assert.Equal("sha256:bbb", runtime.LatestImageId);
        Assert.True(runtime.IsOutdated);
        Assert.Equal(Now, runtime.LastCheckedAt);
    }

    [Fact]
    public void ABackgroundCheckLeavesTheApplyErrorInPlace() {
        var runtime = SelfUpdateService.ApplyCheckResult(
            FailedApply, "sha256:aaa", "sha256:bbb", isOutdated: true,
            acknowledgeApplyError: false, checkedAt: Now);

        Assert.Equal("error", runtime.ApplyStage);
        Assert.Equal(FailedApply.ApplyError, runtime.ApplyError);
        Assert.Equal(Now, runtime.LastCheckedAt);
    }

    [Fact]
    public void AManualCheckNeverTouchesAnInFlightStage() {
        var inFlight = new SelfUpdateRuntime { ApplyStage = "restarting", CoordinatorId = "c0ffee" };

        var runtime = SelfUpdateService.ApplyCheckResult(
            inFlight, "sha256:aaa", "sha256:aaa", isOutdated: false,
            acknowledgeApplyError: true, checkedAt: Now);

        Assert.Equal("restarting", runtime.ApplyStage);
        Assert.Equal("c0ffee", runtime.CoordinatorId);
    }
}
