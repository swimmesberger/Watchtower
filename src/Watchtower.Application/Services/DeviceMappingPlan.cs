using Watchtower.Application.Entities;

namespace Watchtower.Application.Services;

/// <summary>One host device a service is to receive.</summary>
/// <param name="HostPath">Absolute device path on the host.</param>
/// <param name="ContainerPath">Absolute device path inside the container.</param>
/// <param name="Permissions">Cgroup permissions (subset of <c>rwm</c>), or null for the runtime default.</param>
public sealed record ServiceDevice(string HostPath, string ContainerPath, string? Permissions);

/// <summary>Every device one service receives, ordered deterministically.</summary>
/// <param name="ServiceName">The service the devices go to.</param>
/// <param name="Devices">The devices, ordered by container path then host path.</param>
public sealed record ServiceDeviceMappings(string ServiceName, IReadOnlyList<ServiceDevice> Devices) {
    /// <summary>
    /// Supplementary group ids the service's container user needs to open the mapped devices —
    /// the owning groups of the resolved GPU nodes (ADR-0031), ascending. Empty for path-only
    /// mappings, where Watchtower does not know the node's group.
    /// </summary>
    public IReadOnlyList<int> GroupIds { get; init; } = [];
}

/// <summary>
/// Which of a stack's compose services receive which host devices, and the warnings the decision
/// produced (ADR-0030; GPU intents ADR-0031).
/// </summary>
/// <remarks>
/// The runtime-neutral half of device mapping, exactly as <see cref="EnvInjectionPlan"/> and
/// <see cref="ImagePinPlan"/> are for their features (ADR-0010's seam rule): it names no Docker or
/// Compose concept — device paths, supplementary groups and a GPU catalog all have Kubernetes
/// equivalents — so turning a plan into a Compose override stays <c>ComposeOverrideFile</c>'s
/// business.
/// <para>
/// Pure and total, like <see cref="ImagePinPlan"/> and for the same reason: a mapping this plan
/// cannot place — its service is not in the resolved project — becomes a warning rather than a
/// failed deploy, because services come and go with the repository and a leftover row must never
/// take a fleet down.
/// </para>
/// </remarks>
/// <param name="Services">
/// The services receiving devices, ordered by name; services receiving none are absent.
/// Deterministic so a rendered override is diffable between deploys.
/// </param>
/// <param name="Warnings">Operator-facing lines for the deploy output, in deterministic order.</param>
public sealed record DeviceMappingPlan(
    IReadOnlyList<ServiceDeviceMappings> Services,
    IReadOnlyList<string> Warnings) {
    /// <summary>
    /// Operator-facing lines that are expected outcomes rather than problems — above all "this host
    /// has no GPU", which is ADR-0031's feature working, not a misconfiguration. Kept apart from
    /// <see cref="Warnings"/> so a deliberately GPU-less host does not warn on every deploy.
    /// </summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>A plan that maps nothing and has nothing to say — what a stack with no rows uses.</summary>
    public static readonly DeviceMappingPlan Empty = new([], []);

    /// <summary>
    /// Places the stack's stored device mappings — literal paths and GPU intents — onto the services
    /// the engine actually resolved.
    /// </summary>
    /// <remarks>
    /// The match is by service name, ordinal — the same identity <see cref="EnvInjectionPlan"/> keys
    /// on. A GPU intent resolves to every <see cref="HostGpu.IsMappable"/> node of
    /// <paramref name="hostGpus"/> plus the nodes' owning groups; NVIDIA nodes are skipped with a
    /// note (ADR-0031 decision 3). Exact duplicate devices collapse silently, and on a container-path
    /// collision the explicit path mapping wins over a GPU-resolved node — the operator's literal row
    /// is the more deliberate statement. Everything else the set handler already validated at write
    /// time, and re-refusing it here would fail a deploy over a row the operator cannot currently see.
    /// </remarks>
    /// <param name="services">The stack's services as the engine resolved them, in any order.</param>
    /// <param name="mappings">The stack's stored literal device mappings, in any order.</param>
    /// <param name="gpuMappings">The stack's stored GPU intents, in any order; null means none.</param>
    /// <param name="hostGpus">The probed host GPU catalog; null/empty on GPU-less hosts or when the probe failed.</param>
    /// <returns>The plan, ordered deterministically.</returns>
    public static DeviceMappingPlan Create(
        IReadOnlyList<EnvInjectionService> services,
        IReadOnlyList<StackDeviceMapping> mappings,
        IReadOnlyList<StackGpuMapping>? gpuMappings = null,
        IReadOnlyList<HostGpu>? hostGpus = null) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mappings);
        var gpuServices = (gpuMappings ?? [])
            .Select(g => g.Service)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (mappings.Count == 0 && gpuServices.Count == 0) return Empty;

        var known = new HashSet<string>(services.Select(s => s.Name), StringComparer.Ordinal);
        var warnings = new List<string>();
        var notes = new List<string>();

        var pathsByService = new Dictionary<string, List<ServiceDevice>>(StringComparer.Ordinal);
        foreach (var group in mappings
                     .GroupBy(m => m.Service, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal)) {
            if (!known.Contains(group.Key)) {
                warnings.Add(
                    $"Warning: device mapping(s) configured for service '{group.Key}', which is not one "
                    + "of this stack's services — they were not applied.");
                continue;
            }
            pathsByService[group.Key] = [.. group
                .Select(m => new ServiceDevice(m.HostPath, m.ContainerPath, m.Permissions))
                .Distinct()];
        }

        var mappable = (hostGpus ?? []).Where(g => g.IsMappable).OrderBy(g => g.Name, StringComparer.Ordinal).ToList();
        var gpusByService = new Dictionary<string, List<HostGpu>>(StringComparer.Ordinal);
        var gpulessServices = new List<string>();
        foreach (var service in gpuServices) {
            if (!known.Contains(service)) {
                warnings.Add(
                    $"Warning: GPU passthrough configured for service '{service}', which is not one "
                    + "of this stack's services — nothing was mapped.");
                continue;
            }
            if (mappable.Count == 0) gpulessServices.Add(service);
            else gpusByService[service] = mappable;
        }
        if (gpulessServices.Count > 0)
            notes.Add(
                "No mappable host GPU was detected — "
                + string.Join(", ", gpulessServices.Select(s => $"'{s}'"))
                + " get(s) no GPU devices on this host.");
        // Only worth a line when someone actually asked for a GPU on this host.
        if (gpuServices.Any(known.Contains))
            foreach (var skipped in (hostGpus ?? []).Where(g => !g.IsMappable).OrderBy(g => g.Name, StringComparer.Ordinal))
                notes.Add(
                    $"NVIDIA GPU '{skipped.Name}' needs the NVIDIA container toolkit and is not mapped "
                    + "by device path (ADR-0031).");

        var placed = new List<ServiceDeviceMappings>();
        foreach (var name in pathsByService.Keys.Union(gpusByService.Keys, StringComparer.Ordinal)
                     .OrderBy(n => n, StringComparer.Ordinal)) {
            var devices = new List<ServiceDevice>(pathsByService.GetValueOrDefault(name) ?? []);
            var groupIds = new List<int>();
            foreach (var gpu in gpusByService.GetValueOrDefault(name) ?? []) {
                // Explicit-path wins on a shared container path: skip the GPU node, keep its group —
                // the operator plainly wants the device reachable either way.
                if (!devices.Any(d => string.Equals(d.ContainerPath, gpu.Path, StringComparison.Ordinal)))
                    devices.Add(new ServiceDevice(gpu.Path, gpu.Path, null));
                groupIds.Add(gpu.GroupId);
            }
            placed.Add(new ServiceDeviceMappings(
                name,
                [.. devices
                    .OrderBy(d => d.ContainerPath, StringComparer.Ordinal)
                    .ThenBy(d => d.HostPath, StringComparer.Ordinal)]) {
                GroupIds = [.. groupIds.Distinct().Order()],
            });
        }

        return placed.Count == 0 && warnings.Count == 0 && notes.Count == 0
            ? Empty
            : new DeviceMappingPlan(placed, warnings) { Notes = notes };
    }
}
