using System.Net;
using System.Text;
using System.Text.Json;
using Elarion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Ci.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The operator-requested runner recycle (<c>ci.recycleRunner</c> / <c>ci.recycleRunners</c>):
/// idle runners are deregistered at GitHub and removed so the loop respawns them, busy runners are
/// kept unless forced, and a container id only resolves against the repo's own labelled containers
/// — an id from some other repo (or an unmanaged container) is a NotFound, not a removal.
/// </summary>
public sealed class CiRunnerRecycleTests {

    [Fact]
    public async Task RecycleRunner_IdleRunner_IsDeregisteredAndRemoved() {
        var docker = new FakeDockerHandler();
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(With(docker, gitHub));
        var repoId = await SeedRepoAsync(host);
        var container = Runner('a', repoId, runnerId: 42);
        docker.Containers.Add(container);

        var result = await SendAsync(host, new RecycleRunner.Command(repoId, container.Id[..12]));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.True(result.Value.Recycled);
        Assert.False(result.Value.Busy);
        Assert.Equal([42L], gitHub.Deleted);
        Assert.Equal([container.Id], docker.Removed);
    }

    [Fact]
    public async Task RecycleRunner_BusyRunner_IsKeptAndReported() {
        var docker = new FakeDockerHandler();
        var gitHub = new StubGitHubApiClient { BusyRunnerIds = { 42 } };
        using var host = AuthTestHost.Start(With(docker, gitHub));
        var repoId = await SeedRepoAsync(host);
        var container = Runner('a', repoId, runnerId: 42);
        docker.Containers.Add(container);

        var result = await SendAsync(host, new RecycleRunner.Command(repoId, container.Id[..12]));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.False(result.Value.Recycled);
        Assert.True(result.Value.Busy);
        Assert.Empty(docker.Removed);
    }

    [Fact]
    public async Task RecycleRunner_BusyRunner_ForcedIsRemovedAnyway() {
        var docker = new FakeDockerHandler();
        var gitHub = new StubGitHubApiClient { BusyRunnerIds = { 42 } };
        using var host = AuthTestHost.Start(With(docker, gitHub));
        var repoId = await SeedRepoAsync(host);
        var container = Runner('a', repoId, runnerId: 42);
        docker.Containers.Add(container);

        var result = await SendAsync(host, new RecycleRunner.Command(repoId, container.Id[..12], Force: true));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.True(result.Value.Recycled);
        Assert.Equal([container.Id], docker.Removed);
        // GitHub refused the delete; the registration dies with the ephemeral runner.
        Assert.Empty(gitHub.Deleted);
    }

    [Fact]
    public async Task RecycleRunner_UnknownContainer_IsNotFound() {
        var docker = new FakeDockerHandler();
        using var host = AuthTestHost.Start(With(docker, new StubGitHubApiClient()));
        var repoId = await SeedRepoAsync(host);

        var result = await SendAsync(host, new RecycleRunner.Command(repoId, new string('f', 12)));

        Assert.False(result.IsSuccess);
        Assert.Contains("ffffffffffff", result.Error.Message);
    }

    [Fact]
    public async Task RecycleRunner_AnotherReposContainer_IsNotFound() {
        var docker = new FakeDockerHandler();
        using var host = AuthTestHost.Start(With(docker, new StubGitHubApiClient()));
        var repoId = await SeedRepoAsync(host);
        // Same managed label, different repo id: the label filter must keep it out of reach.
        var foreign = Runner('b', repoId + 1, runnerId: 7);
        docker.Containers.Add(foreign);

        var result = await SendAsync(host, new RecycleRunner.Command(repoId, foreign.Id[..12]));

        Assert.False(result.IsSuccess);
        Assert.Empty(docker.Removed);
    }

    [Fact]
    public async Task RecycleRunner_ShortIdPrefix_IsRejected() {
        var docker = new FakeDockerHandler();
        using var host = AuthTestHost.Start(With(docker, new StubGitHubApiClient()));
        var repoId = await SeedRepoAsync(host);

        var result = await SendAsync(host, new RecycleRunner.Command(repoId, "abc"));

        Assert.False(result.IsSuccess);
        Assert.Contains("12", result.Error.Message);
    }

    [Fact]
    public async Task RecycleRunners_RecyclesIdle_KeepsBusy() {
        var docker = new FakeDockerHandler();
        var gitHub = new StubGitHubApiClient { BusyRunnerIds = { 2 } };
        using var host = AuthTestHost.Start(With(docker, gitHub));
        var repoId = await SeedRepoAsync(host);
        docker.Containers.Add(Runner('a', repoId, runnerId: 1));
        docker.Containers.Add(Runner('b', repoId, runnerId: 2));
        docker.Containers.Add(Runner('c', repoId, runnerId: 3));

        var result = await SendAsync(host, new RecycleRunners.Command(repoId));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Equal(2, result.Value.Recycled);
        Assert.Equal(1, result.Value.Busy);
        Assert.Equal(2, docker.Removed.Count);
        Assert.DoesNotContain(FullId('b'), docker.Removed);
    }

    [Fact]
    public async Task RecycleRunners_Forced_RemovesBusyToo() {
        var docker = new FakeDockerHandler();
        var gitHub = new StubGitHubApiClient { BusyRunnerIds = { 2 } };
        using var host = AuthTestHost.Start(With(docker, gitHub));
        var repoId = await SeedRepoAsync(host);
        docker.Containers.Add(Runner('a', repoId, runnerId: 1));
        docker.Containers.Add(Runner('b', repoId, runnerId: 2));

        var result = await SendAsync(host, new RecycleRunners.Command(repoId, Force: true));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Equal(2, result.Value.Recycled);
        Assert.Equal(0, result.Value.Busy);
        Assert.Equal(2, docker.Removed.Count);
    }

    [Fact]
    public async Task RecycleRunners_UnknownRepo_IsNotFound() {
        using var host = AuthTestHost.Start(With(new FakeDockerHandler(), new StubGitHubApiClient()));

        var result = await SendAsync(host, new RecycleRunners.Command(RepoId: 4711));

        Assert.False(result.IsSuccess);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Action<IServiceCollection> With(FakeDockerHandler docker, StubGitHubApiClient gitHub) =>
        services => {
            services.AddRecycleRunner();
            services.AddRecycleRunners();
            services.RemoveAll<GitHubApiClient>();
            services.AddSingleton<GitHubApiClient>(gitHub);
            services.RemoveAll<DockerEngineClient>();
            services.AddSingleton(new DockerEngineClient("1.43", docker, TimeSpan.FromSeconds(5)));
        };

    private static async ValueTask<Result<RecycleRunner.Response>> SendAsync(
        AuthTestHost host, RecycleRunner.Command command) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IHandler<RecycleRunner.Command, Result<RecycleRunner.Response>>>()
            .HandleAsync(command, Ct);
    }

    private static async ValueTask<Result<RecycleRunners.Response>> SendAsync(
        AuthTestHost host, RecycleRunners.Command command) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider
            .GetRequiredService<IHandler<RecycleRunners.Command, Result<RecycleRunners.Response>>>()
            .HandleAsync(command, Ct);
    }

    private static async Task<int> SeedRepoAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var credential = new Credential {
            Name = "runner-admin", Username = "x-access-token", Token = "pat",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Credentials.Add(credential);
        var repo = new CiRepo {
            Owner = "acme", Name = "widgets", Credential = credential, Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CiRepos.Add(repo);
        await db.SaveChangesAsync(Ct);
        return repo.Id;
    }

    private static string FullId(char c) => new(c, 64);

    /// <summary>A running runner container of <paramref name="repoId"/> with the orchestrator's labels.</summary>
    private static FakeContainer Runner(char idChar, int repoId, long runnerId) => new(
        FullId(idChar),
        $"/watchtower-runner-{idChar}",
        new Dictionary<string, string> {
            [CiRunnerOrchestrator.ManagedLabel] = CiRunnerOrchestrator.ManagedLabelValue,
            [CiRunnerOrchestrator.RepoIdLabel] = repoId.ToString(),
            [CiRunnerOrchestrator.RepoLabel] = "acme/widgets",
            [CiRunnerOrchestrator.RunnerIdLabel] = runnerId.ToString(),
            [CiRunnerOrchestrator.SpecHashLabel] = "cafecafecafecafe",
        });

    private sealed record FakeContainer(string Id, string Name, Dictionary<string, string> Labels);

    /// <summary>
    /// A Docker daemon reduced to what the recycle path touches: list-by-label (the filters are
    /// applied, since keeping foreign containers out of reach is part of what is under test), stop,
    /// and remove.
    /// </summary>
    private sealed class FakeDockerHandler : HttpMessageHandler {
        public List<FakeContainer> Containers { get; } = [];
        public List<string> Stopped { get; } = [];
        public List<string> Removed { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) {
            var path = request.RequestUri!.AbsolutePath;

            if (request.Method == HttpMethod.Get && path.EndsWith("/containers/json")) {
                var filters = LabelFilters(request.RequestUri.Query);
                var matches = Containers.Where(c => filters.All(f => {
                    var split = f.IndexOf('=');
                    return split > 0
                        && c.Labels.TryGetValue(f[..split], out var value)
                        && value == f[(split + 1)..];
                }));
                return Json("[" + string.Join(",", matches.Select(ContainerJson)) + "]");
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/stop")) {
                Stopped.Add(IdFrom(path));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            if (request.Method == HttpMethod.Delete && path.Contains("/containers/")) {
                var id = IdFrom(path);
                Removed.Add(id);
                Containers.RemoveAll(c => c.Id == id);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
            return Json("{}");
        }

        private static string[] LabelFilters(string query) {
            var marker = "filters=";
            var start = query.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0) return [];
            var raw = query[(start + marker.Length)..];
            var end = raw.IndexOf('&');
            if (end >= 0) raw = raw[..end];
            using var doc = JsonDocument.Parse(Uri.UnescapeDataString(raw));
            return doc.RootElement.TryGetProperty("label", out var labels)
                ? [.. labels.EnumerateArray().Select(l => l.GetString()!)]
                : [];
        }

        private static string IdFrom(string path) {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var index = Array.IndexOf(segments, "containers");
            return segments[index + 1];
        }

        private static string ContainerJson(FakeContainer c) {
            var labels = string.Join(",", c.Labels.Select(kv =>
                $"{JsonSerializer.Serialize(kv.Key)}:{JsonSerializer.Serialize(kv.Value)}"));
            return "{\"Id\":" + JsonSerializer.Serialize(c.Id)
                + ",\"Names\":[" + JsonSerializer.Serialize(c.Name) + "]"
                + ",\"Image\":\"runner:latest\",\"State\":\"running\",\"Status\":\"Up 5 minutes\""
                + ",\"Created\":0,\"Labels\":{" + labels + "}}";
        }

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    /// <summary>GitHub stub: deletes succeed except for runners marked busy, exactly like the API.</summary>
    private sealed class StubGitHubApiClient : GitHubApiClient {
        public HashSet<long> BusyRunnerIds { get; } = [];
        public List<long> Deleted { get; } = [];

        public override Task<bool> TryDeleteRunnerAsync(
            string owner, string repo, long runnerId, string token, CancellationToken ct = default) {
            if (BusyRunnerIds.Contains(runnerId))
                return Task.FromResult(false);
            Deleted.Add(runnerId);
            return Task.FromResult(true);
        }
    }
}
