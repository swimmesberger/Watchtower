using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Watchtower.Application.Services;

/// <summary>
/// Generates the default BuildKit configuration (<c>buildkitd.default.toml</c>) Watchtower ships
/// into every CI runner (docs/ci-runners/design.md §"Container image builds"). buildx reads this
/// file from <c>$BUILDX_CONFIG/buildkitd.default.toml</c> whenever a workflow's
/// <c>docker/setup-buildx-action</c> does not pass a config of its own, so host facts —
/// which registries are plain-HTTP, which snapshotter actually works on this kernel — live here
/// once instead of being hand-copied into every consuming repo's workflow YAML.
/// </summary>
/// <remarks>
/// The registry list comes from the daemon's own <c>insecure-registries</c> setting
/// (<see cref="DockerEngineClient.GetInsecureRegistriesAsync"/>): the daemon is the authority on
/// which registries this host reaches without TLS, and BuildKit's out-of-daemon workers do not read
/// the daemon's configuration. The snapshotter override exists because BuildKit's <c>auto</c>
/// probe can land on <c>native</c> — no copy-on-write, every layer materialisation a full copy —
/// on hosts whose kernel lacks overlayfs (e.g. Synology DSM), and nothing in a job log says so.
/// </remarks>
public static partial class CiBuildkitConfig {

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex SnapshotterPattern();

    // Registry hosts are host[:port] / IP[:port]; anything outside this set cannot be one and is
    // not representable in a TOML basic string without escaping, so it is skipped instead.
    [GeneratedRegex("^[A-Za-z0-9._-]+(:[0-9]+)?$")]
    private static partial Regex RegistryHostPattern();

    /// <summary>
    /// True when <paramref name="value"/> is a plausible BuildKit OCI-worker snapshotter name
    /// (<c>native</c>, <c>overlayfs</c>, <c>fuse-overlayfs</c>, <c>stargz</c>, …). Shape-checked
    /// rather than allow-listed so a new BuildKit snapshotter does not need a Watchtower release.
    /// </summary>
    public static bool IsValidSnapshotter(string value) => SnapshotterPattern().IsMatch(value);

    /// <summary>Sentinel option value: never emit a snapshotter, leave BuildKit's own probe alone.</summary>
    public const string SnapshotterNone = "none";

    /// <summary>Option value (and the unset default) for host detection.</summary>
    public const string SnapshotterAuto = "auto";

    /// <summary>
    /// Resolves the snapshotter to write into the config, from the operator's
    /// <c>Ci:BuildkitSnapshotter</c> option and two host facts. Null means "emit nothing".
    /// <para>
    /// The default (<paramref name="configured"/> unset or <see cref="SnapshotterAuto"/>) detects:
    /// BuildKit's own <c>auto</c> chain is overlayfs-or-<c>native</c> — it never tries
    /// fuse-overlayfs on its own — so a kernel without overlayfs silently gets the copy-less
    /// <c>native</c> snapshotter even where FUSE is fully available (issue #65, Synology DSM).
    /// Watchtower therefore emits <c>fuse-overlayfs</c> exactly when the kernel lacks overlayfs
    /// but has FUSE, and stays silent everywhere else: when overlayfs exists BuildKit picks it
    /// unaided (and it beats fuse-overlayfs), and when neither exists <c>native</c> is all there
    /// is. <see cref="SnapshotterNone"/> disables detection; an explicit name wins outright.
    /// </para>
    /// </summary>
    /// <param name="configured">The raw option value; whitespace and case are forgiven.</param>
    /// <param name="storageDriver">
    /// The daemon's storage driver. An overlay-family driver proves the kernel has overlayfs even
    /// when <paramref name="procFilesystems"/> is unavailable (e.g. Watchtower on a non-Linux dev
    /// host talking to a remote engine).
    /// </param>
    /// <param name="procFilesystems">
    /// Content of <c>/proc/filesystems</c> as seen by Watchtower — kernel-global, so a
    /// containerised Watchtower still reads the host kernel's list. Empty when unreadable, which
    /// degrades to emitting nothing (today's behaviour, never worse).
    /// </param>
    /// <exception cref="ArgumentException">An explicit name that cannot be a snapshotter.</exception>
    public static string? ResolveSnapshotter(string? configured, string? storageDriver, string procFilesystems) {
        var value = configured?.Trim();
        if (!string.IsNullOrEmpty(value) && !value.Equals(SnapshotterAuto, StringComparison.OrdinalIgnoreCase)) {
            if (value.Equals(SnapshotterNone, StringComparison.OrdinalIgnoreCase))
                return null;
            if (!IsValidSnapshotter(value))
                throw new ArgumentException($"'{value}' is not a valid BuildKit snapshotter name.", nameof(configured));
            return value;
        }

        if (storageDriver?.StartsWith("overlay", StringComparison.OrdinalIgnoreCase) == true)
            return null;
        var filesystems = ListFilesystems(procFilesystems);
        if (filesystems.Contains("overlay"))
            return null;
        return filesystems.Contains("fuse") ? "fuse-overlayfs" : null;
    }

    /// <summary>
    /// Filesystem names from <c>/proc/filesystems</c> — one per line, the name is the last
    /// tab-separated token (<c>nodev\tfuse</c>). Exact tokens, so <c>fuseblk</c>/<c>fusectl</c>
    /// do not count as FUSE support.
    /// </summary>
    private static HashSet<string> ListFilesystems(string procFilesystems) =>
        procFilesystems
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line[(line.LastIndexOf('\t') + 1)..].Trim())
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Builds the TOML content. Always returns a file (a header-only one when there is nothing to
    /// configure), so runner containers keep a uniform shape and an emptied configuration
    /// overwrites a stale one. Registries that do not look like <c>host[:port]</c> are skipped;
    /// <paramref name="snapshotter"/> must be null or pass <see cref="IsValidSnapshotter"/>
    /// (callers validate and log — this throws to refuse silently emitting broken TOML).
    /// </summary>
    public static string Build(IEnumerable<string> insecureRegistries, string? snapshotter) {
        if (snapshotter is not null && !IsValidSnapshotter(snapshotter))
            throw new ArgumentException($"'{snapshotter}' is not a valid BuildKit snapshotter name.", nameof(snapshotter));

        var sb = new StringBuilder();
        sb.Append(
            """
            # Generated by Watchtower — default BuildKit configuration for CI runner builds.
            # Read by `docker buildx create` when the workflow does not pass a config of its own
            # ($BUILDX_CONFIG/buildkitd.default.toml). See docs/ci-runners/design.md.

            """);

        if (snapshotter is not null) {
            sb.Append('\n');
            sb.Append("[worker.oci]\n");
            sb.Append($"  snapshotter = \"{snapshotter}\"\n");
        }

        foreach (var registry in insecureRegistries.Where(r => RegistryHostPattern().IsMatch(r))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(r => r, StringComparer.Ordinal)) {
            // Both flags, mirroring what the daemon's insecure-registries setting means: try HTTPS
            // without certificate verification, fall back to plain HTTP.
            sb.Append('\n');
            sb.Append($"[registry.\"{registry}\"]\n");
            sb.Append("  http = true\n");
            sb.Append("  insecure = true\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Short content stamp used to decide whether a repo's buildx volume already carries this
    /// configuration (see <c>CiRepoRunnerStatus.VolumesReadyStamp</c>).
    /// </summary>
    public static string Stamp(string toml) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(toml)))[..16];
}
