using Watchtower.Application.Config;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Pins are what keep the env-wins configuration layering honest: a <c>WATCHTOWER__*</c> variable wins
/// over the settings store, so the paths it covers must be reported (UI disables the field) and write
/// attempts rejected. These tests pin the name mapping both ways.
/// </summary>
public sealed class EnvironmentSettingPinsTests {
    [Fact]
    public void MapsDoubleUnderscoreVariablesOntoConfigurationPaths() {
        var pins = new EnvironmentSettingPins([
            "WATCHTOWER__AUTH__ENABLED",
            "WATCHTOWER__METRICS__INFLUX__URL",
            "PATH",                       // unrelated → ignored
            "WATCHTOWER_HOST_PROC",       // single underscore: not part of the options tree → never pins
        ]);

        Assert.True(pins.IsPinned(WatchtowerSettingPaths.AuthEnabled));
        Assert.True(pins.IsPinned(WatchtowerSettingPaths.MetricsInfluxUrl));
        Assert.False(pins.IsPinned(WatchtowerSettingPaths.AuthHost));
        Assert.False(pins.IsPinned("Watchtower:Host:Proc"));
    }

    [Fact]
    public void MatchingIsCaseInsensitive_LikeIConfiguration() {
        var pins = new EnvironmentSettingPins(["watchtower__proxy__enabled"]);
        Assert.True(pins.IsPinned(WatchtowerSettingPaths.ProxyEnabled));
    }

    [Fact]
    public void PinnedFiltersToThePinnedSubset_PreservingOrder() {
        var pins = new EnvironmentSettingPins(["WATCHTOWER__AUTH__ENABLED", "WATCHTOWER__AUTH__HOST"]);
        var pinned = pins.Pinned(
            WatchtowerSettingPaths.AuthEnabled,
            WatchtowerSettingPaths.AuthSessionLifetimeHours,
            WatchtowerSettingPaths.AuthHost);
        Assert.Equal([WatchtowerSettingPaths.AuthEnabled, WatchtowerSettingPaths.AuthHost], pinned);
    }

    [Fact]
    public void EnvironmentVariableNameIsTheConventionalSpelling() {
        Assert.Equal("WATCHTOWER__AUTH__ENABLED",
            EnvironmentSettingPins.ToEnvironmentVariableName(WatchtowerSettingPaths.AuthEnabled));
        Assert.Equal("WATCHTOWER__METRICS__INFLUX__COMPOSEPROJECTTAG",
            EnvironmentSettingPins.ToEnvironmentVariableName(WatchtowerSettingPaths.MetricsInfluxComposeProjectTag));
    }

    [Fact]
    public void PinnedErrorNamesTheEnvironmentVariables() {
        var error = EnvironmentSettingPins.PinnedError([WatchtowerSettingPaths.AuthEnabled]);
        Assert.Contains("WATCHTOWER__AUTH__ENABLED", error.Message);
    }
}
