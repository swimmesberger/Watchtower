using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watchtower.Application.Services;

/// <summary>One detected toolchain requirement of a CI repo (e.g. ".NET 10.0" or "Node 22").</summary>
/// <param name="Kind">Toolchain family: <c>dotnet</c>, <c>node</c> or <c>go</c>.</param>
/// <param name="Version">
/// Normalized version: a channel (<c>10.0</c>) for .NET, an exact version or major line for Node,
/// a minor line or exact version for Go. The warmer resolves partial versions to the latest release.
/// </param>
/// <param name="Source">Where the signal came from (<c>workflow</c>, <c>global.json</c>, …).</param>
public sealed record CiToolchain(string Kind, string Version, string Source);

/// <summary>
/// The toolchains a repository's builds need, detected heuristically from its working tree during a
/// stack deploy (docs/ci-runners/design.md). Persisted as JSON on <see cref="Entities.CiRepo"/>;
/// the orchestrator pre-warms the repo's toolcache volume whenever <see cref="ComputeHash"/> changes.
/// </summary>
public sealed record CiToolchainProfile {
    /// <summary>Detected toolchains, sorted (kind, version) so serialization and hash are stable.</summary>
    public required IReadOnlyList<CiToolchain> Toolchains { get; init; }

    /// <summary>True when the tree contains a Dockerfile (a docker-based build is likely).</summary>
    public bool HasDockerfile { get; init; }

    /// <summary>An empty profile: detection ran but found no known toolchain signals.</summary>
    public static readonly CiToolchainProfile Empty = new() { Toolchains = [] };

    /// <summary>True when nothing was detected (still persisted — "checked, found nothing" is a result).</summary>
    [JsonIgnore]
    public bool IsEmpty => Toolchains.Count == 0 && !HasDockerfile;

    public string ToJson() => JsonSerializer.Serialize(this, CiToolchainJsonContext.Default.CiToolchainProfile);

    /// <summary>Deserializes a stored profile; null for null/blank/corrupt JSON (treated as "none").</summary>
    public static CiToolchainProfile? FromJson(string? json) {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try {
            return JsonSerializer.Deserialize(json, CiToolchainJsonContext.Default.CiToolchainProfile);
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>
    /// Stable short hash over the warm-relevant content (toolchain kind+version pairs). Source
    /// attribution and Dockerfile presence are excluded — they don't change what gets installed,
    /// so they must not trigger a re-warm.
    /// </summary>
    public string ComputeHash() {
        var canonical = string.Join(";", Toolchains
            .Select(t => $"{t.Kind}:{t.Version}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexStringLower(bytes)[..16];
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CiToolchainProfile))]
internal sealed partial class CiToolchainJsonContext : JsonSerializerContext;
