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
/// </summary>
public sealed class GitHubApiClient : IDisposable {
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
    public async Task<bool> TryDeleteRunnerAsync(string owner, string repo, long runnerId, string token, CancellationToken ct = default) {
        using var request = NewRequest(HttpMethod.Delete, $"repos/{owner}/{repo}/actions/runners/{runnerId}", token);
        var response = await _client.SendAsync(request, ct);
        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
    }

    /// <summary>
    /// Probes the PAT's scopes for a repo at configuration time so a wrong-scoped credential fails
    /// in <c>ci.addRepo</c> with a clear message instead of at reconcile time. Listing runners
    /// requires the same Administration permission as generating JIT configs.
    /// </summary>
    public async Task<string?> ValidateRepoAccessAsync(string owner, string repo, string token, CancellationToken ct = default) {
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

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(GitHubJitConfigRequest))]
[JsonSerializable(typeof(GitHubJitConfigResponse))]
[JsonSerializable(typeof(List<GitHubRepoInfo>))]
internal sealed partial class GitHubJsonContext : JsonSerializerContext;
