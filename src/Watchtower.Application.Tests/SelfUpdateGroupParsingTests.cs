using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers parsing of the <c>Groups:</c> line from <c>/proc/self/status</c>, whose ids become the
/// coordinator container's <c>GroupAdd</c>. The kernel puts a tab between the label and the ids;
/// a parse that keeps it produces <c>"\t0"</c>, which Docker treats as a group name and rejects
/// with "unable to find group 0: no matching entries in group file" — failing the whole self-update.
/// </summary>
public sealed class SelfUpdateGroupParsingTests {
    [Fact]
    public void StripsTheTabAfterTheLabel() =>
        Assert.Equal(["0"], SelfUpdateService.ParseGroupsLine("Groups:\t0 "));

    [Fact]
    public void SplitsMultipleIdsOnSpaces() =>
        Assert.Equal(
            ["4", "24", "27", "999"],
            SelfUpdateService.ParseGroupsLine("Groups:\t4 24 27 999 "));

    [Fact]
    public void AProcessWithNoSupplementaryGroupsYieldsNoIds() =>
        Assert.Empty(SelfUpdateService.ParseGroupsLine("Groups:\t"));
}
