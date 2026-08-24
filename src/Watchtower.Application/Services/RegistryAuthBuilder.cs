using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Builds a scoped DOCKER_CONFIG directory containing a config.json with
/// base64-encoded credentials for each configured registry.
///
/// Passing DOCKER_CONFIG to a subprocess scopes its registry authentication
/// without touching the global ~/.docker/config.json on the host.
///
/// Host credentials (from the system ~/.docker/config.json or $DOCKER_CONFIG) are merged
/// as a base layer so images already accessible via <c>docker login</c> on the host continue
/// to work. Watchtower-configured credentials take precedence on collision.
/// </summary>
public sealed class RegistryAuthBuilder(WatchtowerDbContext db) {
    /// <summary>
    /// Creates a temporary directory under <c>/tmp</c> containing a valid
    /// docker config.json populated with all configured registry credentials.
    /// The caller is responsible for deleting this directory after use.
    /// </summary>
    /// <returns>Path to the temp directory (the DOCKER_CONFIG value).</returns>
    public string CreateTempConfigDir() {
        var dir = Path.Combine(Path.GetTempPath(), $"watchtower-docker-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        // Start with credentials from the host docker config (e.g. set by docker login).
        // This lets compose pull from any registry the host user is already authenticated with.
        var auths = LoadHostAuths();

        // Apply Watchtower-configured credentials on top (override host credentials on collision).
        var configured = db.Registries
            .AsNoTracking()
            .Where(r => r.CredentialId != null && r.Credential != null)
            .Select(r => new { r.Url, r.Credential!.Username, r.Credential.Token })
            .ToList();
        foreach (var reg in configured) {
            var authValue = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{reg.Username}:{reg.Token}"));
            auths[reg.Url] = new RegistryAuth { Auth = authValue };
        }

        var config = new DockerConfig { Auths = auths };
        var json = JsonSerializer.Serialize(config, DockerConfigJsonContext.Default.DockerConfig);
        File.WriteAllText(Path.Combine(dir, "config.json"), json);
        return dir;
    }

    /// <summary>
    /// The merged registry view used by CI secret sync and the read-only registry listing: host
    /// docker-config auths as the base layer, Watchtower-configured registries on top (same
    /// precedence as <see cref="CreateTempConfigDir"/>). Entries carry decoded credentials —
    /// keep them server-side; DTOs must strip <see cref="ResolvedRegistry.Password"/>.
    /// </summary>
    public IReadOnlyList<ResolvedRegistry> ListResolvedRegistries() {
        var result = new Dictionary<string, ResolvedRegistry>(StringComparer.OrdinalIgnoreCase);
        foreach (var (url, auth) in LoadHostAuths()) {
            var (username, password) = DecodeAuth(auth.Auth);
            result[url] = new ResolvedRegistry(url, username, password, FromHostConfig: true, Name: null, RegistryId: null);
        }

        var configured = db.Registries
            .AsNoTracking()
            .Where(r => r.CredentialId != null && r.Credential != null)
            .Select(r => new { r.Id, r.Name, r.Url, r.Credential!.Username, r.Credential.Token })
            .ToList();
        foreach (var reg in configured)
            result[reg.Url] = new ResolvedRegistry(reg.Url, reg.Username, reg.Token, FromHostConfig: false, reg.Name, reg.Id);

        return result.Values.OrderBy(r => r.Url, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Decodes a docker config <c>auth</c> value (base64 <c>user:pass</c>). Nulls when malformed.</summary>
    private static (string? Username, string? Password) DecodeAuth(string auth) {
        try {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth));
            var separator = decoded.IndexOf(':');
            return separator < 0 ? (null, null) : (decoded[..separator], decoded[(separator + 1)..]);
        } catch (FormatException) {
            return (null, null);
        }
    }

    /// <summary>
    /// Reads the <c>auths</c> section from the host's docker config.json.
    /// Returns an empty dictionary when the file is absent, unreadable, or has no auths.
    /// </summary>
    private static Dictionary<string, RegistryAuth> LoadHostAuths() {
        try {
            var hostConfigPath = GetHostDockerConfigPath();
            if (!File.Exists(hostConfigPath)) return [];

            var json = File.ReadAllText(hostConfigPath);
            var node = JsonNode.Parse(json);
            var authsNode = node?["auths"];
            if (authsNode is null) return [];

            var result = new Dictionary<string, RegistryAuth>();
            foreach (var (registry, value) in authsNode.AsObject()) {
                var auth = value?["auth"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(auth))
                    result[registry] = new RegistryAuth { Auth = auth };
            }
            return result;
        } catch {
            // Host config is optional — silently ignore any parse or IO errors.
            return [];
        }
    }

    /// <summary>
    /// Returns the path to the host's docker config.json.
    /// Resolution order:
    /// <list type="number">
    ///   <item><c>WATCHTOWER_DOCKER_CONFIG</c> — explicit override for containerised deployments where the host
    ///   config is mounted at a custom path (e.g. <c>-v ~/.docker:/host-docker-config:ro -e WATCHTOWER_DOCKER_CONFIG=/host-docker-config</c>).</item>
    ///   <item><c>DOCKER_CONFIG</c> — standard Docker environment variable.</item>
    ///   <item><c>~/.docker</c> — default Docker config directory.</item>
    /// </list>
    /// </summary>
    private static string GetHostDockerConfigPath() {
        var configDir =
            Environment.GetEnvironmentVariable("WATCHTOWER_DOCKER_CONFIG")
            ?? Environment.GetEnvironmentVariable("DOCKER_CONFIG")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".docker");
        return Path.Combine(configDir, "config.json");
    }
}

/// <summary>
/// One registry as CI sync and the registry listing see it: the docker-config host layer merged
/// with Watchtower-configured registries. <see cref="Password"/> never leaves the backend.
/// </summary>
public sealed record ResolvedRegistry(
    string Url, string? Username, string? Password, bool FromHostConfig, string? Name, int? RegistryId);

/// <summary>Represents the structure of a docker config.json file.</summary>
internal sealed record DockerConfig {
    public required Dictionary<string, RegistryAuth> Auths { get; init; }
}

/// <summary>Per-registry auth entry in docker config.json.</summary>
internal sealed record RegistryAuth {
    public required string Auth { get; init; }
}

[JsonSerializable(typeof(DockerConfig))]
[JsonSerializable(typeof(RegistryAuth))]
[JsonSerializable(typeof(Dictionary<string, RegistryAuth>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class DockerConfigJsonContext : JsonSerializerContext;
