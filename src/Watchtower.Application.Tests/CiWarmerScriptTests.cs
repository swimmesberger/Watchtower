using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The warmer script is generated text handed to <c>bash -c</c> inside a container that mounts a
/// shared volume — these tests pin what gets installed, that an unwarmable profile produces no
/// container at all, and that version strings cannot smuggle shell syntax into the script.
/// </summary>
public sealed class CiWarmerScriptTests {
    [Fact]
    public void Build_EmptyProfile_ReturnsNull() {
        Assert.Null(CiWarmerScript.Build(CiToolchainProfile.Empty));
    }

    [Fact]
    public void Build_DockerfileOnlyProfile_ReturnsNull() {
        // A docker-based build needs no toolcache; spawning a warmer for it would be a no-op container.
        var profile = new CiToolchainProfile { Toolchains = [], HasDockerfile = true };
        Assert.Null(CiWarmerScript.Build(profile));
    }

    [Fact]
    public void Build_EmitsOneInstallPerDistinctToolchainVersion() {
        var profile = new CiToolchainProfile {
            Toolchains = [
                new CiToolchain("dotnet", "10.0", "workflow"),
                new CiToolchain("dotnet", "10.0", "global.json"), // duplicate version, one install
                new CiToolchain("dotnet", "8.0", "workflow"),
                new CiToolchain("node", "22", "workflow"),
                new CiToolchain("go", "1.24", "go.mod"),
            ],
        };

        var script = CiWarmerScript.Build(profile);

        Assert.NotNull(script);
        Assert.Equal(["warm_dotnet '10.0'", "warm_dotnet '8.0'"],
            Occurrences(script, "warm_dotnet '"), StringComparer.Ordinal);
        Assert.Contains("warm_node '22'", script);
        Assert.Contains("warm_go '1.24'", script);
        // The script must operate on the volume mount point the runners share.
        Assert.Contains($"TOOL=\"{CiWarmerScript.ToolCacheDir}\"", script);
        // A failed install marks the run failed but later installs still execute.
        Assert.Contains("fail=1", script);
        Assert.Contains("exit $fail", script);
    }

    [Fact]
    public void Build_RejectsVersionsThatAreNotPlainNumbers() {
        // The detector normalizes versions, but the script is the injection boundary: anything that
        // is not digits-and-dots must not reach the shell, whatever upstream produced it.
        var profile = new CiToolchainProfile {
            Toolchains = [
                new CiToolchain("node", "22'; rm -rf /; echo '", "workflow"),
                new CiToolchain("dotnet", "$(curl evil)", "workflow"),
                new CiToolchain("go", "1.24", "go.mod"),
            ],
        };

        var script = CiWarmerScript.Build(profile);

        Assert.NotNull(script);
        Assert.DoesNotContain("rm -rf /;", script);
        Assert.DoesNotContain("$(curl evil)", script);
        // The tainted kinds produce no install call at all; the clean one still does.
        Assert.DoesNotContain("warm_node '", script);
        Assert.DoesNotContain("warm_dotnet '", script);
        Assert.Contains("warm_go '1.24'", script);
    }

    /// <summary>Install invocations starting with <paramref name="prefix"/>, trimmed to their argument.</summary>
    private static string[] Occurrences(string script, string prefix) =>
        script.Split('\n')
            .Where(l => l.StartsWith(prefix, StringComparison.Ordinal))
            .Select(l => l[..(l.IndexOf('\'', prefix.Length) + 1)])
            .ToArray();
}
