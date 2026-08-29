using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>One GPU render node the host exposes, as the probe read it (ADR-0031).</summary>
/// <param name="Name">The node's name, e.g. <c>renderD128</c>.</param>
/// <param name="Path">The host device path, e.g. <c>/dev/dri/renderD128</c>.</param>
/// <param name="VendorId">PCI vendor id as sysfs prints it (<c>0x8086</c> Intel, <c>0x1002</c> AMD, <c>0x10de</c> NVIDIA); empty when unreadable.</param>
/// <param name="Driver">The bound kernel driver (<c>i915</c>, <c>xe</c>, <c>amdgpu</c>, <c>nvidia</c>, …); empty when unreadable.</param>
/// <param name="PciAddress">The PCI address, e.g. <c>0000:00:02.0</c>; empty when unreadable.</param>
/// <param name="GroupId">The GID owning the device node — what the container user must carry to open it.</param>
public sealed record HostGpu(
    string Name, string Path, string VendorId, string Driver, string PciAddress, int GroupId) {
    /// <summary>PCI vendor ids, lower-cased as sysfs prints them.</summary>
    public const string IntelVendorId = "0x8086";
    public const string AmdVendorId = "0x1002";
    public const string NvidiaVendorId = "0x10de";

    /// <summary>
    /// Whether a plain device mapping gives a container working access. NVIDIA is the deliberate
    /// exception (ADR-0031 decision 3): the node without the toolkit-injected user-space driver
    /// fails inconsistently, which is worse than not mapping it.
    /// </summary>
    public bool IsMappable =>
        !string.Equals(VendorId, NvidiaVendorId, StringComparison.OrdinalIgnoreCase)
        && Driver is not ("nvidia" or "nouveau");
}

/// <summary>What one probe run produced: the nodes found, or why nothing could be said.</summary>
/// <param name="Gpus">The render nodes, in node-name order; empty on a GPU-less host.</param>
/// <param name="Error">
/// Why the probe could not run (helper image unpullable, daemon error), or null when it ran — an
/// empty <paramref name="Gpus"/> with a null error genuinely means "this host has no render nodes".
/// </param>
public sealed record HostGpuCatalog(IReadOnlyList<HostGpu> Gpus, string? Error) {
    public static readonly HostGpuCatalog Empty = new([], null);

    /// <summary>
    /// Whether the host has an NVIDIA card at all, from <c>/dev/nvidiactl</c> rather than from the
    /// DRM listing (ADR-0032).
    /// </summary>
    /// <remarks>
    /// NVIDIA is only visible in <see cref="Gpus"/> when <c>nvidia-drm</c> happens to be loaded,
    /// which is common on desktops and not guaranteed on the headless boxes that actually hold the
    /// cards. Keying the diagnostics on the DRM listing therefore stayed silent on exactly the
    /// hosts where the operator most needs to be told which route to take.
    /// </remarks>
    public bool NvidiaPresent { get; init; }

    /// <summary>
    /// Whether the daemon has the NVIDIA container toolkit configured, i.e. whether a GPU
    /// reservation would resolve. Emitting one without it fails the whole deploy, so it gates.
    /// </summary>
    public bool NvidiaRuntimeAvailable { get; init; }

    /// <summary>An NVIDIA card the toolkit can actually hand to a container.</summary>
    public bool NvidiaUsable => NvidiaPresent && NvidiaRuntimeAvailable;
}

/// <summary>
/// Discovers the Docker host's GPU render nodes (ADR-0031). Watchtower's own container does not see
/// the host's <c>/dev</c>, so the probe borrows the backup feature's trick (ADR-0016): a short-lived
/// helper container with the host's <c>/dev</c> and <c>/sys</c> bind-mounted read-only. The default
/// device cgroup denies opening the nodes, so the probe can list and <c>stat</c> but never touch a
/// device — it needs no privileges beyond the two mounts.
/// </summary>
/// <remarks>
/// The result is cached briefly: the UI asks on every Settings visit and a deploy asks once more,
/// while the answer changes about as often as someone reseats a PCI card. Failure is part of the
/// contract, not an exception — a deploy must proceed (GPU-less) past a broken probe, so
/// <see cref="GetAsync"/> reports problems inside the catalog.
/// </remarks>
public sealed class HostGpuProbe(
    DockerEngineClient docker,
    IOptionsMonitor<WatchtowerOptions> options,
    ILogger<HostGpuProbe> logger,
    TimeProvider time) {
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Walks the render nodes via the host's <c>/sys</c> (mounted at <c>/hostsys</c>) and prints one
    /// parseable line per node. Field order matches <see cref="ParseProbeOutput"/>. BusyBox-only
    /// tools, matching the default helper image; every sub-read tolerates absence so one odd node
    /// cannot take down the whole listing.
    /// </summary>
    private const string ProbeScript = """
        for n in /hostsys/class/drm/renderD*; do
          [ -e "$n" ] || continue
          name="${n##*/}"
          node="/hostdev/dri/$name"
          [ -e "$node" ] || continue
          vendor="$(cat "$n/device/vendor" 2>/dev/null)"
          driver="$(sed -n 's/^DRIVER=//p' "$n/device/uevent" 2>/dev/null)"
          pci="$(readlink -f "$n/device" 2>/dev/null)"
          gid="$(stat -c %g "$node" 2>/dev/null)"
          echo "gpu|$name|$vendor|$driver|${pci##*/}|$gid"
        done
        # NVIDIA lives outside DRM: the control node exists whenever the kernel driver is loaded,
        # including on the headless hosts where nvidia-drm is not.
        [ -e /hostdev/nvidiactl ] && echo "nvidia|present"
        exit 0
        """;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HostGpuCatalog? _cached;
    private DateTimeOffset _cachedAt;

    /// <summary>Returns the host's GPU catalog, probing at most once per <see cref="CacheTtl"/>.</summary>
    public async Task<HostGpuCatalog> GetAsync(CancellationToken ct) {
        await _gate.WaitAsync(ct);
        try {
            if (_cached is not null && time.GetUtcNow() - _cachedAt < CacheTtl) return _cached;
            _cached = await ProbeAsync(ct);
            _cachedAt = time.GetUtcNow();
            return _cached;
        } finally {
            _gate.Release();
        }
    }

    private async Task<HostGpuCatalog> ProbeAsync(CancellationToken ct) {
        var image = options.CurrentValue.Backup.HelperImage;
        string? containerId = null;
        try {
            if (!await docker.ImageExistsAsync(image, ct)) {
                logger.LogInformation("Pulling GPU probe helper image {Image}", image);
                await docker.PullImageAsync(image, ct: ct);
            }

            containerId = await docker.CreateContainerAsync(new DockerCreateContainerBody {
                Image = image,
                Cmd = ["sh", "-c", ProbeScript],
                HostConfig = new DockerCreateHostConfig {
                    // The whole of /dev and /sys rather than /dev/dri and /sys/class/drm: both roots
                    // exist on every Linux host, where binding a *missing* source path would make
                    // the daemon create it as a directory on the host — a GPU-less machine would
                    // grow an empty /dev/dri because Watchtower looked at it.
                    Binds = ["/dev:/hostdev:ro", "/sys:/hostsys:ro"],
                    NetworkMode = "none",
                    AutoRemove = false,
                },
            }, name: $"watchtower-gpuprobe-{Guid.NewGuid():N}"[..32], ct);

            await docker.StartContainerAsync(containerId, ct);
            var exitCode = await docker.WaitContainerAsync(containerId, ct);

            var lines = new List<string>();
            await foreach (var line in docker.StreamLogsAsync(containerId, tail: 200, follow: false, ct))
                lines.Add(line);

            if (exitCode != 0) {
                logger.LogWarning("GPU probe helper exited with code {ExitCode}", exitCode);
                return new HostGpuCatalog([], $"The GPU probe helper exited with code {exitCode}.");
            }
            return new HostGpuCatalog(ParseProbeOutput(lines), null) {
                NvidiaPresent = ParseNvidiaPresent(lines),
                NvidiaRuntimeAvailable = await HasNvidiaRuntimeAsync(ct),
            };
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            // The message reaches the Settings UI and the deploy log; the stack trace stays here.
            logger.LogWarning(ex, "GPU probe failed");
            return new HostGpuCatalog([], $"Probing the host for GPUs failed: {ex.Message}");
        } finally {
            if (containerId is not null) {
                try {
                    await docker.RemoveContainerAsync(containerId, CancellationToken.None);
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Failed to remove GPU probe container {ContainerId}", containerId);
                }
            }
        }
    }

    /// <summary>Whether the probe saw an NVIDIA control node. Pure, for the tests' sake.</summary>
    public static bool ParseNvidiaPresent(IReadOnlyList<string> lines) =>
        lines.Any(l => l.Trim() == "nvidia|present");

    /// <summary>
    /// Asks the daemon whether the NVIDIA container toolkit is configured. A failure here is not a
    /// probe failure: it only means the reservation is withheld, which is the safe direction.
    /// </summary>
    private async Task<bool> HasNvidiaRuntimeAsync(CancellationToken ct) {
        try {
            return (await docker.GetEngineInfoAsync(ct)).HasNvidiaRuntime;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            logger.LogDebug(ex, "Could not read the daemon's runtimes; assuming no NVIDIA toolkit");
            return false;
        }
    }

    /// <summary>
    /// Parses the probe's <c>gpu|name|vendor|driver|pci|gid</c> lines. Anything else in the log —
    /// a shell diagnostic, a truncated line — is skipped rather than failed on: a probe that found
    /// two GPUs and one oddity should report two GPUs. Pure, for the tests' sake.
    /// </summary>
    public static IReadOnlyList<HostGpu> ParseProbeOutput(IReadOnlyList<string> lines) {
        var gpus = new List<HostGpu>();
        foreach (var line in lines) {
            var parts = line.Split('|');
            if (parts.Length != 6 || parts[0] != "gpu") continue;
            var name = parts[1].Trim();
            // The GID gates group_add, so a node whose stat failed is dropped rather than mapped
            // half-working: a device the container cannot open is the trap this feature removes.
            if (name.Length == 0 || !int.TryParse(parts[5].Trim(), out var gid)) continue;
            gpus.Add(new HostGpu(
                name,
                $"/dev/dri/{name}",
                parts[2].Trim().ToLowerInvariant(),
                parts[3].Trim(),
                parts[4].Trim(),
                gid));
        }
        return [.. gpus.OrderBy(g => g.Name, StringComparer.Ordinal)];
    }
}
