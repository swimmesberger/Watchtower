using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="ComposeCliService.BuildComposeArgs"/> — the argument list every compose
/// invocation is assembled from.
/// </summary>
/// <remarks>
/// Compose merges the files it is given in the order they appear, so the position of Watchtower's
/// generated override is not cosmetic: after the repository's file it wins for the keys it defines,
/// before it the repository does. Both orderings produce a working deploy and only one of them
/// injects anything, which is precisely the kind of mistake that needs a test rather than a reading.
/// </remarks>
public sealed class ComposeCliArgumentTests {
    [Fact]
    public void TheOverrideFileFollowsTheRepositoryFile() {
        var args = ComposeCliService.BuildComposeArgs(
            "/repo/docker-compose.yml", "shop", "/tmp/env", "/tmp/override.yml", "up", "-d");

        Assert.Equal(
            ["compose",
             "--file", "/repo/docker-compose.yml",
             "--file", "/tmp/override.yml",
             "--project-name", "shop",
             "--env-file", "/tmp/env",
             "up", "-d"],
            args);
    }

    [Fact]
    public void NothingIsAddedWhenThereIsNoOverrideOrEnvFile() {
        var args = ComposeCliService.BuildComposeArgs(
            "/repo/docker-compose.yml", "shop", envFilePath: null, overrideFilePath: null, "down");

        Assert.Equal(
            ["compose", "--file", "/repo/docker-compose.yml", "--project-name", "shop", "down"], args);
    }
}
