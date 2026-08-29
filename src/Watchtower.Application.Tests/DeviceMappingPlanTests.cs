using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="DeviceMappingPlan"/> — placing a stack's stored device mappings (ADR-0030) onto
/// the services the engine resolved. Pure policy, like <see cref="ImagePinPlan"/>: the tolerance cases
/// are the point, because a leftover row must warn rather than fail a deploy.
/// </summary>
public sealed class DeviceMappingPlanTests {
    private static readonly IReadOnlyList<EnvInjectionService> Services =
        [new EnvInjectionService("web"), new EnvInjectionService("transcoder")];

    private static StackDeviceMapping Row(
        string service, string host, string? container = null, string? permissions = null) =>
        new() { Service = service, HostPath = host, ContainerPath = container ?? host, Permissions = permissions };

    [Fact]
    public void Create_ReturnsEmptyForNoMappings() =>
        Assert.Same(DeviceMappingPlan.Empty, DeviceMappingPlan.Create(Services, []));

    /// <summary>
    /// Services in ordinal name order, each service's devices ordered by container then host path —
    /// deterministic, so a rendered override is diffable between deploys.
    /// </summary>
    [Fact]
    public void Create_OrdersServicesAndDevicesDeterministically() {
        var plan = DeviceMappingPlan.Create(Services, [
            Row("web", "/dev/fuse"),
            Row("transcoder", "/dev/ttyUSB0", "/dev/ttyUSB1", "rw"),
            Row("transcoder", "/dev/dri/renderD128"),
        ]);

        Assert.Empty(plan.Warnings);
        Assert.Equal(["transcoder", "web"], plan.Services.Select(s => s.ServiceName));
        Assert.Equal(
            [new ServiceDevice("/dev/dri/renderD128", "/dev/dri/renderD128", null),
             new ServiceDevice("/dev/ttyUSB0", "/dev/ttyUSB1", "rw")],
            plan.Services[0].Devices);
        Assert.Equal([new ServiceDevice("/dev/fuse", "/dev/fuse", null)], plan.Services[1].Devices);
    }

    /// <summary>
    /// A mapping for a service the resolved project does not contain warns and is skipped — services
    /// come and go with the repository, and failing the deploy over a leftover row would take a fleet
    /// down (the <see cref="ImagePinPlan"/> tolerance rule).
    /// </summary>
    [Fact]
    public void Create_WarnsAndSkipsAMappingForAnUnknownService() {
        var plan = DeviceMappingPlan.Create(Services, [
            Row("removed-service", "/dev/dri/renderD128"),
            Row("web", "/dev/fuse"),
        ]);

        var placed = Assert.Single(plan.Services);
        Assert.Equal("web", placed.ServiceName);
        Assert.Equal([new ServiceDevice("/dev/fuse", "/dev/fuse", null)], placed.Devices);
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("'removed-service'", warning, StringComparison.Ordinal);
        Assert.Contains("not applied", warning, StringComparison.Ordinal);
    }

    /// <summary>Nothing placeable still reports why — an all-stale plan is warnings, not silence.</summary>
    [Fact]
    public void Create_ReturnsWarningsOnlyWhenNothingIsPlaceable() {
        var plan = DeviceMappingPlan.Create(Services, [Row("gone", "/dev/fuse")]);

        Assert.Empty(plan.Services);
        Assert.Single(plan.Warnings);
    }

    // ── GPU intents (ADR-0031) ───────────────────────────────────────────────────────────────────

    private static readonly HostGpu IntelGpu =
        new("renderD128", "/dev/dri/renderD128", HostGpu.IntelVendorId, "i915", "0000:00:02.0", 105);
    private static readonly HostGpu AmdGpu =
        new("renderD129", "/dev/dri/renderD129", HostGpu.AmdVendorId, "amdgpu", "0000:03:00.0", 44);
    private static readonly HostGpu NvidiaGpu =
        new("renderD130", "/dev/dri/renderD130", HostGpu.NvidiaVendorId, "nvidia", "0000:04:00.0", 44);

    /// <summary>
    /// A GPU intent resolves to every mappable render node plus the nodes' owning groups — the GID
    /// half is the "device mapped but VAAPI still fails" trap this feature exists to remove.
    /// </summary>
    [Fact]
    public void Create_ResolvesAGpuIntentToMappableNodesAndTheirGroups() {
        var plan = DeviceMappingPlan.Create(
            Services, [],
            [new StackGpuMapping { Service = "transcoder" }],
            [IntelGpu, AmdGpu]);

        var placed = Assert.Single(plan.Services);
        Assert.Equal("transcoder", placed.ServiceName);
        Assert.Equal(
            [new ServiceDevice("/dev/dri/renderD128", "/dev/dri/renderD128", null),
             new ServiceDevice("/dev/dri/renderD129", "/dev/dri/renderD129", null)],
            placed.Devices);
        Assert.Equal([44, 105], placed.GroupIds);
        Assert.Empty(plan.Warnings);
        Assert.Empty(plan.Notes);
    }

    /// <summary>
    /// NVIDIA is skipped with a note (ADR-0031 decision 3): the bare node without the toolkit's
    /// user-space driver fails inconsistently, which is worse than not mapping it.
    /// </summary>
    [Fact]
    public void Create_SkipsNvidiaNodesWithANote() {
        var plan = DeviceMappingPlan.Create(
            Services, [], [new StackGpuMapping { Service = "web" }], [IntelGpu, NvidiaGpu]);

        var placed = Assert.Single(plan.Services);
        Assert.Equal([new ServiceDevice("/dev/dri/renderD128", "/dev/dri/renderD128", null)], placed.Devices);
        Assert.Equal([105], placed.GroupIds);
        var note = Assert.Single(plan.Notes);
        Assert.Contains("NVIDIA", note, StringComparison.Ordinal);
        Assert.Contains("'renderD130'", note, StringComparison.Ordinal);
        Assert.Empty(plan.Warnings);
    }

    /// <summary>
    /// A GPU-less host is the feature working, not a problem: a note names the services, no warning
    /// is raised, and nothing is mapped — the same stack deploys everywhere.
    /// </summary>
    [Fact]
    public void Create_NotesWithoutWarningWhenTheHostHasNoMappableGpu() {
        var plan = DeviceMappingPlan.Create(
            Services, [], [new StackGpuMapping { Service = "web" }], hostGpus: []);

        Assert.Empty(plan.Services);
        Assert.Empty(plan.Warnings);
        var note = Assert.Single(plan.Notes);
        Assert.Contains("No mappable host GPU", note, StringComparison.Ordinal);
        Assert.Contains("'web'", note, StringComparison.Ordinal);
    }

    /// <summary>A GPU intent for a service the project lacks keeps ADR-0030's warning treatment.</summary>
    [Fact]
    public void Create_WarnsForAGpuIntentOnAnUnknownService() {
        var plan = DeviceMappingPlan.Create(
            Services, [], [new StackGpuMapping { Service = "gone" }], [IntelGpu]);

        Assert.Empty(plan.Services);
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("GPU passthrough", warning, StringComparison.Ordinal);
        Assert.Contains("'gone'", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// On a shared container path the explicit row wins — it is the more deliberate statement — but
    /// the GPU's group still travels, so the device stays openable either way.
    /// </summary>
    [Fact]
    public void Create_ExplicitPathWinsOverAGpuNodeOnTheSameTarget() {
        var plan = DeviceMappingPlan.Create(
            Services,
            [Row("web", "/dev/dri/renderD128", permissions: "rw")],
            [new StackGpuMapping { Service = "web" }],
            [IntelGpu]);

        var placed = Assert.Single(plan.Services);
        Assert.Equal(
            [new ServiceDevice("/dev/dri/renderD128", "/dev/dri/renderD128", "rw")],
            placed.Devices);
        Assert.Equal([105], placed.GroupIds);
    }

    /// <summary>Exact duplicate rows collapse silently — they cannot disagree about anything.</summary>
    [Fact]
    public void Create_CollapsesExactDuplicates() {
        var plan = DeviceMappingPlan.Create(Services, [
            Row("web", "/dev/fuse", permissions: "rw"),
            Row("web", "/dev/fuse", permissions: "rw"),
        ]);

        var placed = Assert.Single(plan.Services);
        Assert.Equal([new ServiceDevice("/dev/fuse", "/dev/fuse", "rw")], placed.Devices);
        Assert.Empty(plan.Warnings);
    }

    // ── NVIDIA: the same intent, resolved through the toolkit instead (ADR-0032) ──────────────

    private static HostGpuCatalog NvidiaHost(bool runtime = true) =>
        HostGpuCatalog.Empty with { NvidiaPresent = true, NvidiaRuntimeAvailable = runtime };

    /// <summary>
    /// The common shape: a headless NVIDIA box exposes no render node at all, so the intent has to
    /// resolve to a reservation with no device paths behind it.
    /// </summary>
    [Fact]
    public void Create_ResolvesAGpuIntentToAnNvidiaReservationWithNoDeviceNodes() {
        var plan = DeviceMappingPlan.Create(
            Services, [], [new StackGpuMapping { Service = "transcoder" }], [], NvidiaHost());

        var placed = Assert.Single(plan.Services);
        Assert.True(placed.NvidiaGpus);
        Assert.Empty(placed.Devices);
        Assert.Empty(placed.GroupIds);
        Assert.Empty(plan.Warnings);
        Assert.Contains(plan.Notes, n => n.Contains("container toolkit"));
    }

    /// <summary>
    /// A card without the toolkit must reserve nothing: Compose fails the entire deploy with
    /// "could not select device driver", so the safe direction is to say so and map nothing.
    /// </summary>
    [Fact]
    public void Create_WithoutTheToolkitReservesNothingAndSaysWhy() {
        var plan = DeviceMappingPlan.Create(
            Services, [], [new StackGpuMapping { Service = "transcoder" }], [], NvidiaHost(runtime: false));

        Assert.DoesNotContain(plan.Services, s => s.NvidiaGpus);
        Assert.Empty(plan.Warnings);
        Assert.Contains(plan.Notes, n => n.Contains("no 'nvidia' runtime"));
    }

    /// <summary>An NVIDIA host is not a GPU-less host, even though it has no mappable render node.</summary>
    [Fact]
    public void Create_DoesNotCallAnNvidiaHostGpuless() {
        var plan = DeviceMappingPlan.Create(
            Services, [], [new StackGpuMapping { Service = "transcoder" }], [], NvidiaHost());

        Assert.DoesNotContain(plan.Notes, n => n.Contains("No mappable host GPU"));
    }

    /// <summary>A mixed host gets both mechanisms: render nodes by path, the NVIDIA card reserved.</summary>
    [Fact]
    public void Create_CombinesRenderNodesAndAnNvidiaReservation() {
        var plan = DeviceMappingPlan.Create(
            Services, [], [new StackGpuMapping { Service = "transcoder" }], [IntelGpu], NvidiaHost());

        var placed = Assert.Single(plan.Services);
        Assert.True(placed.NvidiaGpus);
        Assert.Equal([new ServiceDevice("/dev/dri/renderD128", "/dev/dri/renderD128", null)], placed.Devices);
        Assert.Equal([105], placed.GroupIds);
    }

    /// <summary>No intent, no reservation — an NVIDIA host must not push GPUs at every service.</summary>
    [Fact]
    public void Create_ReservesNothingForAServiceThatDidNotAskForAGpu() {
        var plan = DeviceMappingPlan.Create(Services, [], [], [], NvidiaHost());

        Assert.DoesNotContain(plan.Services, s => s.NvidiaGpus);
        Assert.Empty(plan.Notes);
    }
}
