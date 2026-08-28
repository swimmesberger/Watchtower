using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watchtower.Application.Services;

/// <summary>
/// Minimal GitHub REST API client for the CI runners feature (docs/ci-runners/design.md):
/// just-in-time runner registration, runner cleanup, credential validation, and the repo picker.
/// Stateless singleton; the PAT is passed per call (it comes from the repo's <c>Credential</c>)
/// and never leaves this process — runner containers only ever see single-use JIT configs.
/// Unsealed with virtual members where tests stub GitHub out (the <see cref="GitCloneService"/>
/// precedent): the scope probe plus the secrets/variables sync calls; everything else stays
/// non-virtual.
/// </summary>
public class GitHubApiClient : IDisposable {
    private readonly HttpClient _client;

    public GitHubApiClient() {
        _client = new HttpClient { BaseAddress = new Uri("https://api.github.com/") };
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("watchtower-ci", "1.0"));
    }

    /// <summary>
    /// Mints a single-use JIT config for one ephemeral runner via
    /// <c>POST /repos/{owner}/{repo}/actions/runners/generate-jitconfig</c>.
    /// Requires a fine-grained PAT with repository Administration (write).
    /// </summary>
    public async Task<GitHubJitRunner> GenerateJitConfigAsync(
        string owner, string repo, string runnerName, IReadOnlyList<string> labels, string token,
        CancellationToken ct = default) {
        var body = new GitHubJitConfigRequest {
            Name = runnerName,
            RunnerGroupId = 1,
            Labels = labels.ToArray(),
        };
        var json = JsonSerializer.Serialize(body, GitHubJsonContext.Default.GitHubJitConfigRequest);
        using var request = NewRequest(HttpMethod.Post, $"repos/{owner}/{repo}/actions/runners/generate-jitconfig", token);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var result = await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.GitHubJitConfigResponse, ct)
            ?? throw new InvalidOperationException("Null response generating JIT config");
        return new GitHubJitRunner(result.Runner.Id, result.Runner.Name, result.EncodedJitConfig);
    }

    /// <summary>
    /// Deletes a registered runner. Best-effort cleanup for runners torn down while idle —
    /// GitHub also purges never-connected/finished JIT runners on its own after a while.
    /// </summary>
    public virtual async Task<bool> TryDeleteRunnerAsync(string owner, string repo, long runnerId, string token, CancellationToken ct = default) {
        using var request = NewRequest(HttpMethod.Delete, $"repos/{owner}/{repo}/actions/runners/{runnerId}", token);
        var response = await _client.SendAsync(request, ct);
        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
    }

    /// <summary>
    /// Probes the PAT's scopes for a repo at configuration time so a wrong-scoped credential fails
    /// in <c>ci.addRepo</c> with a clear message instead of at reconcile time. Listing runners
    /// requires the same Administration permission as generating JIT configs.
    /// </summary>
    public virtual async Task<string?> ValidateRepoAccessAsync(string owner, string repo, string token, CancellationToken ct = default) {
        using var request = NewRequest(HttpMethod.Get, $"repos/{owner}/{repo}/actions/runners?per_page=1", token);
        var response = await _client.SendAsync(request, ct);
        return response.StatusCode switch {
            HttpStatusCode.OK => null,
            HttpStatusCode.NotFound => $"Repository {owner}/{repo} not found or the PAT has no access to it (fine-grained PATs must explicitly include the repository).",
            HttpStatusCode.Unauthorized => "The PAT is invalid or expired.",
            HttpStatusCode.Forbidden => "The PAT lacks the repository Administration permission required to register runners.",
            var code => $"Unexpected GitHub API response {(int)code} while validating access.",
        };
    }

    /// <summary>Repos the PAT can see, for the add-repo picker (most recently pushed first).</summary>
    public async Task<IReadOnlyList<GitHubRepoInfo>> ListAccessibleReposAsync(string token, CancellationToken ct = default) {
        using var request = NewRequest(HttpMethod.Get, "user/repos?per_page=100&sort=pushed", token);
        var response = await _client.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.ListGitHubRepoInfo, ct) ?? [];
    }

    /// <summary>
    /// The message a caller sees when the PAT cannot reach one of the two Actions permissions writing
    /// needs. Public because it is the string tests must assert on: a suite that paraphrases it in a
    /// stub proves only that the stub was wired, which is how a stale "registry sync" wording survived
    /// into the release-sync path unnoticed.
    /// </summary>
    /// <param name="feature">
    /// What needs the permission, as a noun phrase that reads after "the": <c>"registry sync"</c>,
    /// <c>"release secret sync"</c>.
    /// </param>
    /// <param name="permission">The GitHub permission's own name: <c>"Secrets"</c> or <c>"Variables"</c>.</param>
    public static string MissingActionsPermissionMessage(string feature, string permission) =>
        $"The PAT cannot access the repository's Actions {permission.ToLowerInvariant()} — the "
        + $"{feature} needs the fine-grained PAT to also carry the repository {permission} "
        + "(read and write) permission.";

    /// <summary>
    /// Probes whether the PAT can reach the repo's Actions secrets and variables — called when a
    /// sync registry is selected, and when release-secret sync is switched on, so a wrong-scoped PAT
    /// fails in the UI with the missing permission named instead of as a background sync error. Read
    /// access is provable cheaply (the public key and the variables list); write access is not provable
    /// without writing, but fine-grained PATs only offer read-only vs read-and-write per permission, so
    /// proving read catches the real case (permission not granted at all). Null when both probes pass.
    /// </summary>
    /// <param name="feature">
    /// Named in the failure message so the caller's own feature is what the operator is told to grant
    /// the permission for. Both callers write the same two permissions, but only one of them is ever
    /// the reason the operator is standing in front of this error.
    /// </param>
    public virtual async Task<string?> ValidateSecretsAccessAsync(
        string owner, string repo, string token, string feature, CancellationToken ct = default) {
        var secrets = await ProbeAsync(
            $"repos/{owner}/{repo}/actions/secrets/public-key", "Secrets", feature, token, ct);
        if (secrets is not null) return secrets;
        return await ProbeAsync(
            $"repos/{owner}/{repo}/actions/variables?per_page=1", "Variables", feature, token, ct);
    }

    private async Task<string?> ProbeAsync(
        string url, string permission, string feature, string token, CancellationToken ct) {
        using var request = NewRequest(HttpMethod.Get, url, token);
        var response = await _client.SendAsync(request, ct);
        return response.StatusCode switch {
            HttpStatusCode.OK => null,
            HttpStatusCode.Unauthorized => "The PAT is invalid or expired.",
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound =>
                MissingActionsPermissionMessage(feature, permission),
            var code => $"Unexpected GitHub API response {(int)code} while probing Actions {permission.ToLowerInvariant()} access.",
        };
    }

    /// <summary>
    /// Fetches the repo's Actions public key for sealed-box secret encryption
    /// (<c>GET /repos/{owner}/{repo}/actions/secrets/public-key</c>). Requires Secrets (read).
    /// </summary>
    public virtual async Task<GitHubActionsPublicKey> GetActionsPublicKeyAsync(
        string owner, string repo, string token, CancellationToken ct = default) {
        using var request = NewRequest(HttpMethod.Get, $"repos/{owner}/{repo}/actions/secrets/public-key", token);
        var response = await _client.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, GitHubJsonContext.Default.GitHubActionsPublicKey, ct)
            ?? throw new InvalidOperationException("Null response fetching the Actions public key");
    }

    /// <summary>
    /// Creates or updates one repo Actions secret with an already sealed value
    /// (<c>PUT /repos/{owner}/{repo}/actions/secrets/{name}</c>). Requires Secrets (write).
    /// </summary>
    public virtual async Task PutActionsSecretAsync(
        string owner, string repo, string name, string encryptedValue, string keyId, string token,
        CancellationToken ct = default) {
        var body = new GitHubSecretPutRequest { EncryptedValue = encryptedValue, KeyId = keyId };
        var json = JsonSerializer.Serialize(body, GitHubJsonContext.Default.GitHubSecretPutRequest);
        using var request = NewRequest(HttpMethod.Put, $"repos/{owner}/{repo}/actions/secrets/{name}", token);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    /// <summary>
    /// Creates or updates one repo Actions variable — PATCH first (the common re-sync path),
    /// falling back to POST when the variable does not exist yet. Requires Variables (write).
    /// </summary>
    public virtual async Task SetActionsVariableAsync(
        string owner, string repo, string name, string value, string token, CancellationToken ct = default) {
        var body = new GitHubVariableRequest { Name = name, Value = value };
        var json = JsonSerializer.Serialize(body, GitHubJsonContext.Default.GitHubVariableRequest);

        using var patch = NewRequest(HttpMethod.Patch, $"repos/{owner}/{repo}/actions/variables/{name}", token);
        patch.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _client.SendAsync(patch, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) {
            using var post = NewRequest(HttpMethod.Post, $"repos/{owner}/{repo}/actions/variables", token);
            post.Content = new StringContent(json, Encoding.UTF8, "application/json");
            response = await _client.SendAsync(post, ct);
        }
        await EnsureSuccessAsync(response, ct);
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string token) {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct) {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(ct);
        var snippet = body.Length > 300 ? body[..300] : body;
        throw new HttpRequestException($"GitHub API {(int)response.StatusCode} {response.ReasonPhrase}: {snippet}");
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>Result of a JIT registration: the runner GitHub created plus its single-use config blob.</summary>
public sealed record GitHubJitRunner(long RunnerId, string Name, string EncodedJitConfig);

public sealed record GitHubJitConfigRequest {
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("runner_group_id")] public required int RunnerGroupId { get; init; }
    [JsonPropertyName("labels")] public required string[] Labels { get; init; }
}

public sealed record GitHubJitConfigResponse {
    [JsonPropertyName("runner")] public required GitHubRunnerInfo Runner { get; init; }
    [JsonPropertyName("encoded_jit_config")] public required string EncodedJitConfig { get; init; }
}

public sealed record GitHubRunnerInfo {
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = "";
}

public sealed record GitHubRepoInfo {
    [JsonPropertyName("full_name")] public string FullName { get; init; } = "";
    [JsonPropertyName("private")] public bool Private { get; init; }
    [JsonPropertyName("default_branch")] public string DefaultBranch { get; init; } = "";
    [JsonPropertyName("pushed_at")] public DateTimeOffset? PushedAt { get; init; }
}

/// <summary>Repo Actions public key for sealed-box secret encryption.</summary>
public sealed record GitHubActionsPublicKey {
    [JsonPropertyName("key_id")] public required string KeyId { get; init; }
    [JsonPropertyName("key")] public required string Key { get; init; }
}

public sealed record GitHubSecretPutRequest {
    [JsonPropertyName("encrypted_value")] public required string EncryptedValue { get; init; }
    [JsonPropertyName("key_id")] public required string KeyId { get; init; }
}

public sealed record GitHubVariableRequest {
    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("value")] public required string Value { get; init; }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GitHubJitConfigRequest))]
[JsonSerializable(typeof(GitHubJitConfigResponse))]
[JsonSerializable(typeof(List<GitHubRepoInfo>))]
[JsonSerializable(typeof(GitHubActionsPublicKey))]
[JsonSerializable(typeof(GitHubSecretPutRequest))]
[JsonSerializable(typeof(GitHubVariableRequest))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
