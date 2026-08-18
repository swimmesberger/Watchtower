using System.Text;
using System.Text.RegularExpressions;

namespace Watchtower.Application.Services;

/// <summary>
/// Renders the bash script a one-shot warmer container runs to pre-install a repo's detected
/// toolchains into its shared toolcache volume (docs/ci-runners/design.md). The layout matches what
/// the <c>setup-*</c> actions probe — <c>node/{version}/{arch}</c> + <c>.complete</c> marker for
/// setup-node/setup-go's <c>RUNNER_TOOL_CACHE</c> lookup, and a <c>dotnet</c> install dir that
/// runners expose via <c>DOTNET_INSTALL_DIR</c> so dotnet-install finds the SDK already present.
/// Jobs then skip the SDK download entirely, which is the whole point of warming.
///
/// Security: the script only downloads public SDK releases (nodejs.org, dot.net, go.dev). The
/// warmer container gets no PAT, no JIT config, no Docker socket — nothing but the cache volume.
/// A warm failure is surfaced on the repo and never blocks runners or deploys.
/// </summary>
public static partial class CiWarmerScript {
    /// <summary>Where the toolcache volume is mounted in runner and warmer containers alike.</summary>
    public const string ToolCacheDir = "/home/runner/_work/_tool";

    /// <summary>
    /// Builds the warmer script for <paramref name="profile"/>, or null when the profile names no
    /// warmable toolchain (nothing to do → no container is spawned).
    /// </summary>
    public static string? Build(CiToolchainProfile profile) {
        var dotnet = Versions(profile, "dotnet");
        var node = Versions(profile, "node");
        var go = Versions(profile, "go");
        if (dotnet.Count == 0 && node.Count == 0 && go.Count == 0)
            return null;

        var script = new StringBuilder();
        script.Append($$"""
            #!/bin/bash
            # Watchtower CI toolcache warmer — installs detected SDKs into the shared toolcache
            # volume so setup-* actions get a local cache hit. Public downloads only; no secrets.
            set -uo pipefail
            TOOL="{{ToolCacheDir}}"
            case "$(uname -m)" in
              x86_64) ARCH=x64 ;;
              aarch64|arm64) ARCH=arm64 ;;
              *) echo "unsupported architecture: $(uname -m)"; exit 1 ;;
            esac
            mkdir -p "$TOOL"
            fail=0

            warm_node() {
              local ver="$1"
              case "$ver" in
                *.*.*) ;;
                *)
                  # Major line -> resolve the latest release from the dist index.
                  ver="$(curl -fsSL "https://nodejs.org/dist/latest-v${ver}.x/SHASUMS256.txt" \
                    | grep -o "node-v[0-9.]*-linux-${ARCH}\.tar\.gz" | head -1 \
                    | sed -E 's/^node-v([0-9.]+)-linux.*/\1/')" || return 1
                  [ -n "$ver" ] || return 1 ;;
              esac
              local dest="$TOOL/node/${ver}/${ARCH}"
              if [ -f "${dest}.complete" ]; then echo "node ${ver} already cached"; return 0; fi
              echo "warming node ${ver}"
              rm -rf "$dest" && mkdir -p "$dest" || return 1
              curl -fsSL "https://nodejs.org/dist/v${ver}/node-v${ver}-linux-${ARCH}.tar.gz" \
                | tar -xz -C "$dest" --strip-components=1 || return 1
              touch "${dest}.complete"
            }

            warm_dotnet() {
              local channel="$1"
              if ls -d "$TOOL/dotnet/sdk/${channel}."* >/dev/null 2>&1; then
                echo "dotnet ${channel} already cached"; return 0
              fi
              echo "warming dotnet ${channel}"
              curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh || return 1
              bash /tmp/dotnet-install.sh --channel "$channel" --install-dir "$TOOL/dotnet" --no-path || return 1
            }

            warm_go() {
              local ver="$1"
              case "$ver" in
                *.*.*) ;;
                *)
                  # Minor line -> resolve the latest release from the download index.
                  ver="$(curl -fsSL 'https://go.dev/dl/?mode=json' \
                    | grep -o "\"go${ver}\(\.[0-9]*\)*\"" | head -1 | tr -d '"' | sed 's/^go//')" || return 1
                  [ -n "$ver" ] || return 1 ;;
              esac
              local goarch="$ARCH"
              [ "$goarch" = "x64" ] && goarch=amd64
              local dest="$TOOL/go/${ver}/${ARCH}"
              if [ -f "${dest}.complete" ]; then echo "go ${ver} already cached"; return 0; fi
              echo "warming go ${ver}"
              rm -rf "$dest" && mkdir -p "$dest" || return 1
              curl -fsSL "https://go.dev/dl/go${ver}.linux-${goarch}.tar.gz" \
                | tar -xz -C "$dest" --strip-components=1 || return 1
              touch "${dest}.complete"
            }

            """);

        foreach (var version in dotnet)
            script.AppendLine($"warm_dotnet '{version}' || {{ echo 'WARM FAILED: dotnet {version}'; fail=1; }}");
        foreach (var version in node)
            script.AppendLine($"warm_node '{version}' || {{ echo 'WARM FAILED: node {version}'; fail=1; }}");
        foreach (var version in go)
            script.AppendLine($"warm_go '{version}' || {{ echo 'WARM FAILED: go {version}'; fail=1; }}");
        script.AppendLine("exit $fail");
        return script.ToString();
    }

    /// <summary>
    /// The distinct, shell-safe versions of one toolchain kind. Versions are re-validated against a
    /// strict numeric pattern here — they are interpolated into a shell script, so this is the
    /// injection boundary even though the detector already normalizes them.
    /// </summary>
    private static List<string> Versions(CiToolchainProfile profile, string kind) =>
        profile.Toolchains
            .Where(t => t.Kind == kind && SafeVersionRegex().IsMatch(t.Version))
            .Select(t => t.Version)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    [GeneratedRegex(@"^\d+(\.\d+){0,3}$")]
    private static partial Regex SafeVersionRegex();
}
