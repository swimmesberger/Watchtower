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

    /// <summary>
    /// The NVIDIA marker rides the same output as the render nodes, because NVIDIA is usually not
    /// among them: the control node exists whenever the kernel driver is loaded, while a DRM node
    /// only appears when nvidia-drm is — which on a headless host it often is not (ADR-0032).
    /// </summary>
    [Fact]
    public void Parse_DetectsNvidiaFromTheControlNodeWithoutADrmNode() {
        var lines = new[] { "gpu|renderD128|0x8086|i915|0000:00:02.0|105", "nvidia|present" };

        Assert.True(HostGpuProbe.ParseNvidiaPresent(lines));
        // The marker is not a render node and must not be parsed as one.
        Assert.Single(HostGpuProbe.ParseProbeOutput(lines));
    }

    [Fact]
    public void Parse_ReportsNoNvidiaWhenTheMarkerIsAbsent() {
        Assert.False(HostGpuProbe.ParseNvidiaPresent(["gpu|renderD128|0x8086|i915|0000:00:02.0|105"]));
        Assert.False(HostGpuProbe.ParseNvidiaPresent([]));
    }

    /// <summary>
    /// A card is only usable when the toolkit can hand it over; the driver alone is not enough,
    /// and emitting a reservation without it fails the whole deploy.
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void NvidiaUsable_RequiresBothTheCardAndTheToolkit(bool present, bool runtime, bool usable) {
        var catalog = HostGpuCatalog.Empty with { NvidiaPresent = present, NvidiaRuntimeAvailable = runtime };

        Assert.Equal(usable, catalog.NvidiaUsable);
    }

    /// <summary>The toolkit registers itself as a daemon runtime; that is the only signal we get.</summary>
    [Fact]
    public void EngineInfo_ReadsTheNvidiaRuntime() {
        Assert.True(new DockerEngineInfo {
            Runtimes = new() { ["runc"] = new DockerRuntimeInfo(), ["nvidia"] = new DockerRuntimeInfo() },
        }.HasNvidiaRuntime);
        Assert.False(new DockerEngineInfo {
            Runtimes = new() { ["runc"] = new DockerRuntimeInfo() },
        }.HasNvidiaRuntime);
        Assert.False(new DockerEngineInfo().HasNvidiaRuntime);
    }
}
