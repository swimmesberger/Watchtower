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

    /// <summary>Accepted synonym for <see cref="SnapshotterNone"/>: BuildKit's own probe IS the auto.</summary>
    public const string SnapshotterAuto = "auto";

    /// <summary>
    /// Resolves the snapshotter to write into the config from the operator's
    /// <c>Ci:BuildkitSnapshotter</c> option. Null means "emit nothing" — the default, and
    /// deliberately so: BuildKit's own <c>auto</c> already probes overlayfs and then
    /// fuse-overlayfs <em>with a real test mount</em> before falling back to <c>native</c>
    /// (moby/buildkit <c>cmd/buildkitd/main_oci_worker.go</c>; the fuse-overlayfs
    /// <c>Supported()</c> mounts read-only multiple lowerdirs), so nothing Watchtower could
    /// detect from the outside beats that evidence — it could only agree, or wrongly override.
    /// An explicit name is written as-is and makes buildkitd <em>skip</em> that functional check
    /// entirely: buildkitd then starts cleanly without ever proving a mount works, and a wrong
    /// name turns quietly-slow builds into failing ones at the first layer mount. That is why an
    /// explicit value stays opt-in, for an operator who has verified the snapshotter with a real
    /// mount on a host whose probe is demonstrably wrong (or who needs one the probe never tries,
    /// e.g. <c>stargz</c>). <see cref="SnapshotterNone"/> and <see cref="SnapshotterAuto"/> both
    /// resolve to emit-nothing.
    /// </summary>
    /// <param name="configured">The raw option value; whitespace and case are forgiven.</param>
    /// <exception cref="ArgumentException">An explicit name that cannot be a snapshotter.</exception>
    public static string? ResolveSnapshotter(string? configured) {
        var value = configured?.Trim();
        if (string.IsNullOrEmpty(value)
            || value.Equals(SnapshotterAuto, StringComparison.OrdinalIgnoreCase)
            || value.Equals(SnapshotterNone, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!IsValidSnapshotter(value))
            throw new ArgumentException($"'{value}' is not a valid BuildKit snapshotter name.", nameof(configured));
        return value;
    }

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
