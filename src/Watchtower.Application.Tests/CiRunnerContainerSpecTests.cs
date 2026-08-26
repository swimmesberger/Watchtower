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

    private static CiRepo Repo(bool allowDockerSocket = false) => new() {
        Id = 7,
        Owner = "acme",
        Name = "widgets",
        AllowDockerSocket = allowDockerSocket,
    };

    [Fact]
    public void ToolCache_LivesOutsideTheRunnerWorkDirectory() {
        Assert.False(CiWarmerScript.ToolCacheDir.StartsWith("/home/runner/_work", StringComparison.Ordinal));
        Assert.False(CiRunnerOrchestrator.PkgCacheDir.StartsWith("/home/runner/_work", StringComparison.Ordinal));
    }

    [Fact]
    public void RunnerContainer_MountsNothingUnderTheWorkDirectory() {
        var body = CiRunnerOrchestrator.BuildRunnerContainerBody(Repo(), "runner:latest", "jit", 42);

        Assert.NotNull(body.HostConfig?.Binds);
        foreach (var bind in body.HostConfig!.Binds!) {
            var target = bind.Split(':')[1];
            Assert.False(target.StartsWith("/home/runner/_work", StringComparison.Ordinal),
                $"bind '{bind}' would make dockerd create /home/runner/_work as root");
        }
    }

    [Fact]
    public void RunnerContainer_PointsTheToolCacheEnvAtTheVolume() {
        var body = CiRunnerOrchestrator.BuildRunnerContainerBody(Repo(), "runner:latest", "jit", 42);

        Assert.Contains($"RUNNER_TOOL_CACHE={CiWarmerScript.ToolCacheDir}", body.Env!);
        Assert.Contains($"DOTNET_INSTALL_DIR={CiWarmerScript.ToolCacheDir}/dotnet", body.Env!);
    }

    [Fact]
    public void RunnerContainer_MountsTheDockerSocketOnlyWhenAllowed() {
        const string socketBind = "/var/run/docker.sock:/var/run/docker.sock";
        var without = CiRunnerOrchestrator.BuildRunnerContainerBody(Repo(), "runner:latest", "jit", 42);
        var with = CiRunnerOrchestrator.BuildRunnerContainerBody(Repo(allowDockerSocket: true), "runner:latest", "jit", 42);

        Assert.DoesNotContain(socketBind, without.HostConfig!.Binds!);
        Assert.Contains(socketBind, with.HostConfig!.Binds!);
    }

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
