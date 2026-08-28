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
public sealed record ServiceDeviceMappings(string ServiceName, IReadOnlyList<ServiceDevice> Devices);

/// <summary>
/// Which of a stack's compose services receive which host devices, and the warnings the decision
/// produced (ADR-0030).
/// </summary>
/// <remarks>
/// The runtime-neutral half of device mapping, exactly as <see cref="EnvInjectionPlan"/> and
/// <see cref="ImagePinPlan"/> are for their features (ADR-0010's seam rule): it names no Docker or
/// Compose concept, so a Kubernetes engine could apply the same plan natively. Turning a plan into a
/// Compose override is <c>ComposeOverrideFile</c>'s business.
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
    /// <summary>A plan that maps nothing and has nothing to say — what a stack with no rows uses.</summary>
    public static readonly DeviceMappingPlan Empty = new([], []);

    /// <summary>
    /// Places the stack's stored device mappings onto the services the engine actually resolved.
    /// </summary>
    /// <remarks>
    /// The match is by service name, ordinal — the same identity <see cref="EnvInjectionPlan"/> keys
    /// on. Exact duplicate rows collapse silently (they cannot disagree about anything); everything
    /// else the set handler already validated at write time, and re-refusing it here would fail a
    /// deploy over a row the operator cannot currently see.
    /// </remarks>
    /// <param name="services">The stack's services as the engine resolved them, in any order.</param>
    /// <param name="mappings">The stack's stored device mappings, in any order.</param>
    /// <returns>The plan, ordered deterministically.</returns>
    public static DeviceMappingPlan Create(
        IReadOnlyList<EnvInjectionService> services, IReadOnlyList<StackDeviceMapping> mappings) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(mappings);
        if (mappings.Count == 0) return Empty;

        var known = new HashSet<string>(services.Select(s => s.Name), StringComparer.Ordinal);
        var placed = new List<ServiceDeviceMappings>();
        var warnings = new List<string>();

        foreach (var group in mappings
                     .GroupBy(m => m.Service, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal)) {
            if (!known.Contains(group.Key)) {
                warnings.Add(
                    $"Warning: device mapping(s) configured for service '{group.Key}', which is not one "
                    + "of this stack's services — they were not applied.");
                continue;
            }
            var devices = group
                .Select(m => new ServiceDevice(m.HostPath, m.ContainerPath, m.Permissions))
                .Distinct()
                .OrderBy(d => d.ContainerPath, StringComparer.Ordinal)
                .ThenBy(d => d.HostPath, StringComparer.Ordinal)
                .ToList();
            placed.Add(new ServiceDeviceMappings(group.Key, devices));
        }

        return placed.Count == 0 && warnings.Count == 0 ? Empty : new DeviceMappingPlan(placed, warnings);
    }
}
