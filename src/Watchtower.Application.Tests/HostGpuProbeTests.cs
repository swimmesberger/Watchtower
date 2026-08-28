using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="HostGpuProbe.ParseProbeOutput"/> — the pure half of the ADR-0031 probe. The
/// container side is a fixed BusyBox script; this is where its output contract is pinned.
/// </summary>
public sealed class HostGpuProbeTests {
    /// <summary>The shape the probe script actually emits, one line per render node.</summary>
    [Fact]
    public void Parse_ReadsWellFormedLines() {
        var gpus = HostGpuProbe.ParseProbeOutput([
            "gpu|renderD129|0x1002|amdgpu|0000:03:00.0|44",
            "gpu|renderD128|0x8086|i915|0000:00:02.0|105",
        ]);

        Assert.Equal(
            [new HostGpu("renderD128", "/dev/dri/renderD128", "0x8086", "i915", "0000:00:02.0", 105),
             new HostGpu("renderD129", "/dev/dri/renderD129", "0x1002", "amdgpu", "0000:03:00.0", 44)],
            gpus);
    }

    /// <summary>
    /// Anything that is not a complete gpu line — shell noise, a node whose <c>stat</c> failed and
    /// left the GID empty — is skipped, not failed on: two good GPUs and one oddity is two GPUs.
    /// A GID-less node in particular must not be mapped, because <c>group_add</c> is what makes the
    /// mapping actually work.
    /// </summary>
    [Fact]
    public void Parse_SkipsNoiseAndIncompleteLines() {
        var gpus = HostGpuProbe.ParseProbeOutput([
            "sh: something unrelated",
            "gpu|renderD128|0x8086|i915|0000:00:02.0|",
            "gpu|renderD129|0x8086|i915|0000:00:02.0",
            "gpu|renderD130|0x8086|i915|0000:00:02.0|105",
        ]);

        Assert.Equal([new HostGpu("renderD130", "/dev/dri/renderD130", "0x8086", "i915", "0000:00:02.0", 105)], gpus);
    }

    [Fact]
    public void Parse_ReturnsNothingForNoOutput() => Assert.Empty(HostGpuProbe.ParseProbeOutput([]));

    /// <summary>NVIDIA is identified by vendor id or driver — either alone marks the node unmappable.</summary>
    [Theory]
    [InlineData("0x10de", "nvidia", false)]
    [InlineData("0x10de", "nouveau", false)]
    [InlineData("0x8086", "i915", true)]
    [InlineData("0x8086", "xe", true)]
    [InlineData("0x1002", "amdgpu", true)]
    public void IsMappable_ExcludesNvidia(string vendorId, string driver, bool mappable) =>
        Assert.Equal(
            mappable,
            new HostGpu("renderD128", "/dev/dri/renderD128", vendorId, driver, "0000:00:02.0", 105).IsMappable);
}
