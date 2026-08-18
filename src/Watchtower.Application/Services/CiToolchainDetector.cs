using System.Text.Json;
using System.Text.RegularExpressions;

namespace Watchtower.Application.Services;

/// <summary>
/// Heuristic toolchain detection over a cloned working tree (docs/ci-runners/design.md). Runs
/// during stack deploys — the clone is already on disk, so detection costs one directory walk.
/// Every parser is best-effort: unreadable or malformed files contribute nothing, and the result
/// is at worst an empty profile. Detection must never fail a deploy or block runners.
///
/// Signal strength: versions named by <c>.github/workflows/*.yml</c> <c>setup-*</c> steps are what
/// the jobs will actually install, so when a workflow names versions for a toolchain kind, those
/// win and manifest-derived versions (global.json, .nvmrc, go.mod, …) for that kind are dropped.
/// </summary>
public static partial class CiToolchainDetector {
    private const string DotnetKind = "dotnet";
    private const string NodeKind = "node";
    private const string GoKind = "go";

    private static readonly string[] SkippedDirs =
        [".git", "node_modules", "bin", "obj", "dist", "build", "out", "vendor", ".idea", ".vscode"];
    private const int MaxWalkDepth = 5;
    private const int MaxWalkEntries = 20_000;

    /// <summary>Detects the toolchain profile of the tree rooted at <paramref name="rootDir"/>.</summary>
    public static CiToolchainProfile Detect(string rootDir) {
        var workflow = new List<CiToolchain>();
        var manifest = new List<CiToolchain>();
        var hasDockerfile = false;

        // Workflows — the strongest signal.
        var workflowsDir = Path.Combine(rootDir, ".github", "workflows");
        if (SafeDirectoryExists(workflowsDir)) {
            foreach (var file in SafeEnumerateFiles(workflowsDir)) {
                if (!file.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                    && !file.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (SafeReadLines(file) is { } lines)
                    workflow.AddRange(ParseWorkflow(lines));
            }
        }

        // Root manifests.
        if (SafeReadText(Path.Combine(rootDir, "global.json")) is { } globalJson)
            manifest.AddRange(ParseGlobalJson(globalJson));
        if (SafeReadText(Path.Combine(rootDir, ".nvmrc")) is { } nvmrc
            && NormalizeNodeVersion(nvmrc.Trim()) is { } nvmrcVersion)
            manifest.Add(new CiToolchain(NodeKind, nvmrcVersion, ".nvmrc"));
        if (SafeReadText(Path.Combine(rootDir, "package.json")) is { } packageJson)
            manifest.AddRange(ParsePackageJson(packageJson));
        if (SafeReadText(Path.Combine(rootDir, "go.mod")) is { } goMod)
            manifest.AddRange(ParseGoMod(goMod));

        // Walk for .csproj TFMs and Dockerfiles (bounded; skips dependency/output dirs).
        foreach (var file in Walk(rootDir)) {
            var name = Path.GetFileName(file);
            if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".Dockerfile", StringComparison.OrdinalIgnoreCase)) {
                hasDockerfile = true;
            } else if (name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                       && SafeReadText(file) is { } csproj) {
                manifest.AddRange(ParseCsproj(csproj));
            }
        }

        // Per kind: workflow-sourced versions win; manifests only fill kinds no workflow mentions.
        var workflowKinds = workflow.Select(t => t.Kind).ToHashSet(StringComparer.Ordinal);
        var merged = workflow.Concat(manifest.Where(t => !workflowKinds.Contains(t.Kind)))
            .DistinctBy(t => (t.Kind, t.Version))
            .OrderBy(t => t.Kind, StringComparer.Ordinal)
            .ThenBy(t => t.Version, StringComparer.Ordinal)
            .ToList();

        return new CiToolchainProfile { Toolchains = merged, HasDockerfile = hasDockerfile };
    }

    // ── Workflow parsing ─────────────────────────────────────────────────────

    /// <summary>
    /// Line-based scan for <c>uses: actions/setup-{dotnet,node,go}</c> steps and the
    /// <c>*-version:</c> input that follows. Not a YAML parser by design: workflows are hand-written
    /// YAML where the interesting values are on adjacent lines, and a heuristic that reads 95% of
    /// real files beats a dependency parsing 100% of them. Matrix expressions are ignored.
    /// </summary>
    internal static List<CiToolchain> ParseWorkflow(IReadOnlyList<string> lines) {
        var result = new List<CiToolchain>();
        for (var i = 0; i < lines.Count; i++) {
            var match = SetupActionRegex().Match(lines[i]);
            if (!match.Success)
                continue;
            var kind = match.Groups["kind"].Value switch {
                "dotnet" => DotnetKind,
                "node" => NodeKind,
                "go" => GoKind,
                _ => null,
            };
            if (kind is null)
                continue;

            // Scan the step's remaining lines (until the next step/uses) for the version input.
            for (var j = i + 1; j < lines.Count && j <= i + 20; j++) {
                var line = lines[j];
                if (line.Contains("uses:", StringComparison.Ordinal) || StepStartRegex().IsMatch(line))
                    break;
                var versionMatch = VersionInputRegex().Match(line);
                if (!versionMatch.Success || versionMatch.Groups["kind"].Value != match.Groups["kind"].Value)
                    continue;

                var value = versionMatch.Groups["value"].Value.Trim();
                if (value is "|" or "|-" or ">" or ">-") {
                    // Block scalar: each more-indented following line is one version.
                    var baseIndent = IndentOf(line);
                    for (var k = j + 1; k < lines.Count && IndentOf(lines[k]) > baseIndent; k++)
                        AddNormalized(result, kind, lines[k].Trim(), "workflow");
                } else if (value.StartsWith('[')) {
                    foreach (var item in value.Trim('[', ']').Split(','))
                        AddNormalized(result, kind, item.Trim(), "workflow");
                } else {
                    AddNormalized(result, kind, value, "workflow");
                }
                break;
            }
        }
        return result;
    }

    private static void AddNormalized(List<CiToolchain> result, string kind, string rawVersion, string source) {
        var version = kind switch {
            DotnetKind => NormalizeDotnetVersion(rawVersion),
            NodeKind => NormalizeNodeVersion(rawVersion),
            GoKind => NormalizeGoVersion(rawVersion),
            _ => null,
        };
        if (version is not null)
            result.Add(new CiToolchain(kind, version, source));
    }

    private static int IndentOf(string line) => line.Length - line.TrimStart().Length;

    // ── Manifest parsing ─────────────────────────────────────────────────────

    /// <summary>global.json → the pinned SDK's channel (e.g. <c>10.0.100</c> → <c>10.0</c>).</summary>
    internal static List<CiToolchain> ParseGlobalJson(string json) {
        try {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions {
                AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
            });
            if (doc.RootElement.TryGetProperty("sdk", out var sdk)
                && sdk.ValueKind == JsonValueKind.Object
                && sdk.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                && NormalizeDotnetVersion(version.GetString()!) is { } normalized)
                return [new CiToolchain(DotnetKind, normalized, "global.json")];
        } catch (JsonException) { /* malformed → no signal */ }
        return [];
    }

    /// <summary>package.json — Node is implied; <c>engines.node</c> narrows the major when present.</summary>
    internal static List<CiToolchain> ParsePackageJson(string json) {
        try {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions {
                AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return [];
            if (doc.RootElement.TryGetProperty("engines", out var engines)
                && engines.ValueKind == JsonValueKind.Object
                && engines.TryGetProperty("node", out var node)
                && node.ValueKind == JsonValueKind.String) {
                // Ranges like ">=22 <23" — the first number sequence is the intended major.
                var major = FirstNumberRegex().Match(node.GetString()!);
                if (major.Success)
                    return [new CiToolchain(NodeKind, major.Value, "package.json")];
            }
        } catch (JsonException) {
            return [];
        }
        return [];
    }

    /// <summary>go.mod → the <c>go 1.x[.y]</c> directive as a minor line.</summary>
    internal static List<CiToolchain> ParseGoMod(string content) {
        foreach (var line in content.Split('\n')) {
            var match = GoDirectiveRegex().Match(line);
            if (match.Success && NormalizeGoVersion(match.Groups["version"].Value) is { } version)
                return [new CiToolchain(GoKind, version, "go.mod")];
        }
        return [];
    }

    /// <summary>csproj TargetFramework(s) → .NET channels (<c>net10.0</c> → <c>10.0</c>).</summary>
    internal static List<CiToolchain> ParseCsproj(string content) {
        var result = new List<CiToolchain>();
        foreach (Match match in TargetFrameworkRegex().Matches(content))
            foreach (var tfm in match.Groups["value"].Value.Split(';'))
                if (TfmRegex().Match(tfm.Trim()) is { Success: true } m)
                    result.Add(new CiToolchain(DotnetKind, m.Groups["version"].Value, "csproj"));
        return result;
    }

    // ── Version normalization ────────────────────────────────────────────────

    /// <summary>To a .NET channel <c>major.minor</c>: "10" → "10.0", "8.0.x"/"8.0.100" → "8.0".</summary>
    internal static string? NormalizeDotnetVersion(string raw) {
        var v = raw.Trim().Trim('"', '\'').TrimStart('v');
        var match = NumericPrefixRegex().Match(v);
        if (!match.Success)
            return null;
        var parts = match.Value.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : $"{parts[0]}.0";
    }

    /// <summary>Exact versions (<c>22.1.0</c>) stay exact; anything looser becomes the major line.</summary>
    internal static string? NormalizeNodeVersion(string raw) {
        var v = raw.Trim().Trim('"', '\'').TrimStart('v');
        var match = NumericPrefixRegex().Match(v);
        if (!match.Success)
            return null;
        var parts = match.Value.Split('.');
        return parts.Length >= 3 && v == match.Value ? match.Value : parts[0];
    }

    /// <summary>Go minor line or exact version: "1.24.x" → "1.24", "1.24.1" stays.</summary>
    internal static string? NormalizeGoVersion(string raw) {
        var v = raw.Trim().Trim('"', '\'').TrimStart('v');
        var match = NumericPrefixRegex().Match(v);
        if (!match.Success || !match.Value.Contains('.'))
            return null;
        return match.Value;
    }

    // ── File-system helpers (every failure degrades to "no signal") ──────────

    /// <summary>Bounded breadth-first walk skipping dependency/output directories.</summary>
    private static IEnumerable<string> Walk(string rootDir) {
        var pending = new Queue<(string Dir, int Depth)>();
        pending.Enqueue((rootDir, 0));
        var yielded = 0;
        while (pending.Count > 0) {
            var (dir, depth) = pending.Dequeue();
            foreach (var file in SafeEnumerateFiles(dir)) {
                if (++yielded > MaxWalkEntries)
                    yield break;
                yield return file;
            }
            if (depth >= MaxWalkDepth)
                continue;
            foreach (var sub in SafeEnumerateDirectories(dir)) {
                var name = Path.GetFileName(sub);
                if (!SkippedDirs.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && !name.StartsWith('.'))
                    pending.Enqueue((sub, depth + 1));
            }
        }
    }

    private static bool SafeDirectoryExists(string path) {
        try { return Directory.Exists(path); } catch { return false; }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string dir) {
        try { return Directory.EnumerateFiles(dir).ToList(); } catch { return []; }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string dir) {
        try { return Directory.EnumerateDirectories(dir).ToList(); } catch { return []; }
    }

    private static string? SafeReadText(string path) {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; }
    }

    private static IReadOnlyList<string>? SafeReadLines(string path) {
        try { return File.ReadAllLines(path); } catch { return null; }
    }

    [GeneratedRegex(@"uses:\s*['""]?actions/setup-(?<kind>dotnet|node|go)@")]
    private static partial Regex SetupActionRegex();

    [GeneratedRegex(@"^\s*-\s+(name|id|run|env):")]
    private static partial Regex StepStartRegex();

    [GeneratedRegex(@"(?<kind>dotnet|node|go)-version:\s*(?<value>[^#\r\n]*)")]
    private static partial Regex VersionInputRegex();

    [GeneratedRegex(@"^\s*go\s+(?<version>[\d.]+)\s*$")]
    private static partial Regex GoDirectiveRegex();

    [GeneratedRegex(@"<TargetFrameworks?>(?<value>[^<]+)</TargetFrameworks?>")]
    private static partial Regex TargetFrameworkRegex();

    [GeneratedRegex(@"^net(?<version>\d+\.\d+)")]
    private static partial Regex TfmRegex();

    [GeneratedRegex(@"^\d+(\.\d+)*")]
    private static partial Regex NumericPrefixRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex FirstNumberRegex();
}
