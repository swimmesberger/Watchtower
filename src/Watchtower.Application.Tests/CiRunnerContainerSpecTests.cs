using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Guards the runner/warmer container specs against the mount-permission trap that broke real jobs:
/// dockerd creates missing mountpoint parents as root, so a cache mount under
/// <c>/home/runner/_work</c> left the runner user unable to create <c>_work/_temp</c>
/// (UnauthorizedAccessException in the runner's TempDirectoryManager). Fresh named volumes are
/// root-owned too, which is why a root volume-init container chowns them first.
/// </summary>
public sealed class CiRunnerContainerSpecTests {

    private static CiRepo Repo(bool allowDockerSocket = false, string? extraLabels = null) => new() {
        Id = 7,
        Owner = "acme",
        Name = "widgets",
        AllowDockerSocket = allowDockerSocket,
        ExtraLabels = extraLabels,
    };

    private static readonly string[] HostGroups = ["27", "999"];

    private static DockerCreateContainerBody Body(CiRepo repo, string image = "runner:latest") =>
        CiRunnerOrchestrator.BuildRunnerContainerBody(repo, image, "jit", 42, HostGroups);

    [Fact]
    public void ToolCache_LivesOutsideTheRunnerWorkDirectory() {
        Assert.False(CiWarmerScript.ToolCacheDir.StartsWith("/home/runner/_work", StringComparison.Ordinal));
        Assert.False(CiRunnerOrchestrator.PkgCacheDir.StartsWith("/home/runner/_work", StringComparison.Ordinal));
    }

    [Fact]
    public void RunnerContainer_MountsNothingUnderTheWorkDirectory() {
        var body = Body(Repo());

        Assert.NotNull(body.HostConfig?.Binds);
        foreach (var bind in body.HostConfig!.Binds!) {
            var target = bind.Split(':')[1];
            Assert.False(target.StartsWith("/home/runner/_work", StringComparison.Ordinal),
                $"bind '{bind}' would make dockerd create /home/runner/_work as root");
        }
    }

    [Fact]
    public void RunnerContainer_PointsTheToolCacheEnvAtTheVolume() {
        var body = Body(Repo());

        Assert.Contains($"RUNNER_TOOL_CACHE={CiWarmerScript.ToolCacheDir}", body.Env!);
        Assert.Contains($"DOTNET_INSTALL_DIR={CiWarmerScript.ToolCacheDir}/dotnet", body.Env!);
    }

    [Fact]
    public void RunnerContainer_MountsTheDockerSocketOnlyWhenAllowed() {
        const string socketBind = "/var/run/docker.sock:/var/run/docker.sock";
        var without = Body(Repo());
        var with = Body(Repo(allowDockerSocket: true));

        Assert.DoesNotContain(socketBind, without.HostConfig!.Binds!);
        Assert.Contains(socketBind, with.HostConfig!.Binds!);
    }

    /// <summary>
    /// Mounting the socket is only half the grant: the image's <c>runner</c> user is in a
    /// <c>docker</c> group with a fixed id of 123, never the host's, so without Watchtower's own
    /// supplementary ids every <c>docker</c> call in a job fails with "permission denied while
    /// trying to connect to the Docker daemon socket".
    /// </summary>
    [Fact]
    public void RunnerContainer_JoinsTheHostDockerGroupsOnlyWithTheSocket() {
        Assert.Null(Body(Repo()).HostConfig!.GroupAdd);
        Assert.Equal(HostGroups, Body(Repo(allowDockerSocket: true)).HostConfig!.GroupAdd);
    }

    [Fact]
    public void RunnerContainer_CarriesTheSpecHashOfTheSettingsItWasSpawnedWith() {
        var repo = Repo();
        Assert.Equal(
            CiRunnerOrchestrator.ComputeSpecHash(repo, "runner:latest"),
            Body(repo).Labels![CiRunnerOrchestrator.SpecHashLabel]);
    }

    /// <summary>
    /// The hash is what makes the reconcile loop retire an idle runner after a settings change —
    /// every setting baked into the container at spawn time has to move it, or the change silently
    /// waits for the current runner to consume one more job.
    /// </summary>
    [Theory]
    [InlineData("runner:latest", true, null)]
    [InlineData("custom/runner:1", false, null)]
    [InlineData("runner:latest", false, "gpu")]
    public void SpecHash_ChangesWithEverySettingBakedIntoTheContainer(
        string image, bool allowDockerSocket, string? extraLabels) {
        var baseline = CiRunnerOrchestrator.ComputeSpecHash(Repo(), "runner:latest");

        Assert.NotEqual(baseline, CiRunnerOrchestrator.ComputeSpecHash(Repo(allowDockerSocket, extraLabels), image));
    }

    [Fact]
    public void SpecHash_IsStableForUnchangedSettings() =>
        Assert.Equal(
            CiRunnerOrchestrator.ComputeSpecHash(Repo(allowDockerSocket: true), "runner:latest"),
            CiRunnerOrchestrator.ComputeSpecHash(Repo(allowDockerSocket: true), "runner:latest"));

    [Fact]
    public void VolumeInitContainer_ChownsBothCacheVolumesAsRoot() {
        var repo = Repo();
        var body = CiRunnerOrchestrator.BuildVolumeInitContainerBody(repo, "runner:latest");

        Assert.Equal("root", body.User);
        Assert.Equal(CiRunnerOrchestrator.VolumeInitLabelValue, body.Labels![CiRunnerOrchestrator.ManagedLabel]);
        Assert.Equal("none", body.HostConfig!.NetworkMode);
        Assert.Contains($"{CiRunnerOrchestrator.ToolVolumeName(repo)}:{CiRunnerOrchestrator.VolumeInitMountRoot}/tool",
            body.HostConfig.Binds!);
        Assert.Contains($"{CiRunnerOrchestrator.PkgVolumeName(repo)}:{CiRunnerOrchestrator.VolumeInitMountRoot}/pkg",
            body.HostConfig.Binds!);
        Assert.Equal("chown", body.Cmd![0]);
        Assert.Equal("runner:runner", body.Cmd![1]);
    }
}
