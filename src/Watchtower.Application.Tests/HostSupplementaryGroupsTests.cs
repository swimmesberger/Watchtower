using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers parsing of the <c>Groups:</c> line from <c>/proc/self/status</c>, whose ids become the
/// <c>GroupAdd</c> of every container Watchtower spawns with the docker socket mounted (the
/// self-update coordinator and the CI runners of docker-socket repos). The kernel puts a tab
/// between the label and the ids; a parse that keeps it produces <c>"\t0"</c>, which Docker treats
/// as a group name and rejects with "unable to find group 0: no matching entries in group file" —
/// failing the container start outright.
/// </summary>
public sealed class HostSupplementaryGroupsTests {
    [Fact]
    public void StripsTheTabAfterTheLabel() =>
        Assert.Equal(["0"], HostSupplementaryGroups.ParseGroupsLine("Groups:\t0 "));

    [Fact]
    public void SplitsMultipleIdsOnSpaces() =>
        Assert.Equal(
            ["4", "24", "27", "999"],
            HostSupplementaryGroups.ParseGroupsLine("Groups:\t4 24 27 999 "));

    [Fact]
    public void AProcessWithNoSupplementaryGroupsYieldsNoIds() =>
        Assert.Empty(HostSupplementaryGroups.ParseGroupsLine("Groups:\t"));
}
