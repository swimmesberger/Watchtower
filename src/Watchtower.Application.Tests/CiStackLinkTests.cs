using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Ci;
using Watchtower.Application.Modules.Ci.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the stack↔CI link through the real generated handler pipelines: parsing the stack's
/// repository URL into the shared <c>owner/name</c> key, the up-front PAT scope probe with its
/// named-permission error, credential fallback to the stack's clone credential, and the invariant
/// that stacks deploying the same repository converge on one <see cref="CiRepo"/>.
/// </summary>
public sealed class CiStackLinkTests {
    private const string RepoUrl = "https://github.com/acme/shop.git";

    /// <summary>Both link handlers plus a GitHub stub, registered the way the generated module does.</summary>
    private static Action<IServiceCollection> WithCiLink(StubGitHubApiClient gitHub) => services => {
        services.AddGetStackCi();
        services.AddEnableForStack();
        services.RemoveAll<GitHubApiClient>();
        services.AddSingleton<GitHubApiClient>(gitHub);
    };

    // ── ci.enableForStack ────────────────────────────────────────────────────

    [Fact]
    public async Task Enable_CreatesTheCiRepo_UsingTheStacksCloneCredentialByDefault() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "deploy-bot");
        var stackId = await AddStackAsync(host, "shop", RepoUrl, credentialId);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<EnableForStack.Command, EnableForStack.Response>(
            scope.ServiceProvider, new EnableForStack.Command(stackId));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Equal("acme", result.Value.Repo.Owner);
        Assert.Equal("shop", result.Value.Repo.Name);
        Assert.Equal(credentialId, result.Value.Repo.CredentialId);
        Assert.True(result.Value.Repo.Enabled);
        Assert.Null(result.Value.Repo.Toolchain); // fills on the next deploy's detection pass
        Assert.Equal([("acme", "shop")], gitHub.Probes);
    }

    [Fact]
    public async Task Enable_FailsNamingTheMissingPermission_WhenTheClonePatCannotManageRunners() {
        // The scope probe answers what GitHub answers for a Contents-only fine-grained PAT.
        var gitHub = new StubGitHubApiClient {
            AccessError = "The PAT lacks the repository Administration permission required to register runners.",
        };
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "clone-only");
        var stackId = await AddStackAsync(host, "shop", RepoUrl, credentialId);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<EnableForStack.Command, EnableForStack.Response>(
            scope.ServiceProvider, new EnableForStack.Command(stackId));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        // The message must name the credential, the missing permission, and the way out.
        Assert.Contains("clone-only", result.Error.Message);
        Assert.Contains("Administration", result.Error.Message);
        Assert.Contains("Choose or create a credential", result.Error.Message);
        await AssertNoCiRepoAsync(host);
    }

    [Fact]
    public async Task Enable_FailsWhenTheStackHasNoCredentialAndNoneIsChosen() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var stackId = await AddStackAsync(host, "shop", RepoUrl, credentialId: null);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<EnableForStack.Command, EnableForStack.Response>(
            scope.ServiceProvider, new EnableForStack.Command(stackId));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains("Administration (read and write)", result.Error.Message);
        Assert.Empty(gitHub.Probes); // nothing to probe without a credential
    }

    [Fact]
    public async Task Enable_RejectsNonGitHubRepositories() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "bot");
        var stackId = await AddStackAsync(host, "shop", "https://gitea.local/acme/shop.git", credentialId);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<EnableForStack.Command, EnableForStack.Response>(
            scope.ServiceProvider, new EnableForStack.Command(stackId));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains("github.com", result.Error.Message);
    }

    [Fact]
    public async Task Enable_TwoStacksOfTheSameRepository_ShareOneCiRepo() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "bot");
        var stagingId = await AddStackAsync(host, "shop-staging", RepoUrl, credentialId);
        // Same repo, different URL spelling — the parsed owner/name is the identity.
        var prodId = await AddStackAsync(host, "shop-prod", "git@github.com:acme/shop.git", credentialId);

        int firstRepoId;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var first = await SendAsync<EnableForStack.Command, EnableForStack.Response>(
                scope.ServiceProvider, new EnableForStack.Command(stagingId));
            Assert.True(first.IsSuccess);
            firstRepoId = first.Value.Repo.Id;
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var second = await SendAsync<EnableForStack.Command, EnableForStack.Response>(
                scope.ServiceProvider, new EnableForStack.Command(prodId));
            Assert.True(second.IsSuccess);
            // Linking, not duplicating: one runner pool and one toolcache for the repository.
            Assert.Equal(firstRepoId, second.Value.Repo.Id);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.Equal(1, await db.CiRepos.CountAsync(Ct));
        }
        // The second enable reuses the already-validated credential — no second probe.
        Assert.Equal([("acme", "shop")], gitHub.Probes);
    }

    [Fact]
    public async Task Enable_ReenablesADisabledRepoInsteadOfConflicting() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "bot");
        var stackId = await AddStackAsync(host, "shop", RepoUrl, credentialId);
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            db.CiRepos.Add(new CiRepo {
                Owner = "acme", Name = "shop", CredentialId = credentialId,
                Enabled = false, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<EnableForStack.Command, EnableForStack.Response>(
                scope.ServiceProvider, new EnableForStack.Command(stackId));
            Assert.True(result.IsSuccess);
            Assert.True(result.Value.Repo.Enabled);
        }
    }

    // ── ci.getStackCi ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStackCi_ReportsParseResultAndLink() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "bot");
        var gitHubStackId = await AddStackAsync(host, "shop", RepoUrl, credentialId);
        var foreignStackId = await AddStackAsync(host, "legacy", "https://gitea.local/acme/legacy.git", credentialId);

        await using var scope = host.Services.CreateAsyncScope();

        var unlinked = await SendAsync<GetStackCi.Query, GetStackCi.Response>(
            scope.ServiceProvider, new GetStackCi.Query(gitHubStackId));
        Assert.True(unlinked.IsSuccess);
        Assert.True(unlinked.Value.Ci.IsGitHub);
        Assert.Equal(("acme", "shop"), (unlinked.Value.Ci.Owner, unlinked.Value.Ci.Name));
        Assert.Null(unlinked.Value.Ci.Repo);

        var foreign = await SendAsync<GetStackCi.Query, GetStackCi.Response>(
            scope.ServiceProvider, new GetStackCi.Query(foreignStackId));
        Assert.True(foreign.IsSuccess);
        Assert.False(foreign.Value.Ci.IsGitHub);
        Assert.Null(foreign.Value.Ci.Repo);

        var enabled = await SendAsync<EnableForStack.Command, EnableForStack.Response>(
            scope.ServiceProvider, new EnableForStack.Command(gitHubStackId));
        Assert.True(enabled.IsSuccess);

        var linked = await SendAsync<GetStackCi.Query, GetStackCi.Response>(
            scope.ServiceProvider, new GetStackCi.Query(gitHubStackId));
        Assert.True(linked.IsSuccess);
        Assert.Equal(enabled.Value.Repo.Id, linked.Value.Ci.Repo?.Id);
    }

    [Fact]
    public async Task GetStackCi_SurfacesTheDetectedToolchainProfile() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "bot");
        var stackId = await AddStackAsync(host, "shop", RepoUrl, credentialId);
        var profile = new CiToolchainProfile {
            Toolchains = [new CiToolchain("dotnet", "10.0", "workflow")], HasDockerfile = true,
        };
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            db.CiRepos.Add(new CiRepo {
                Owner = "acme", Name = "shop", CredentialId = credentialId, Enabled = true,
                ToolchainProfileJson = profile.ToJson(),
                ToolchainDetectedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<GetStackCi.Query, GetStackCi.Response>(
                scope.ServiceProvider, new GetStackCi.Query(stackId));

            Assert.True(result.IsSuccess);
            var toolchain = result.Value.Ci.Repo?.Toolchain;
            Assert.NotNull(toolchain);
            Assert.Equal([new CiToolchainDto("dotnet", "10.0", "workflow")], toolchain.Toolchains);
            Assert.True(toolchain.HasDockerfile);
            // Detected but never warmed → the UI shows the warm as outstanding, not failed.
            Assert.Equal("pending", toolchain.WarmStatus);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>().HandleAsync(request, Ct);

    private static async Task<int> AddCredentialAsync(AuthTestHost host, string name) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var credential = new Credential {
            Name = name, Username = "x-access-token", Token = $"token-{name}",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Credentials.Add(credential);
        await db.SaveChangesAsync(Ct);
        return credential.Id;
    }

    private static async Task<int> AddStackAsync(AuthTestHost host, string name, string repositoryUrl, int? credentialId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = name,
            ComposeProjectName = name,
            // The repository and the clone credential the CI link resolves from live on the product now.
            Product = TestProducts.New(name, repositoryUrl, credentialId: credentialId),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);
        return stack.Id;
    }

    private static async Task AssertNoCiRepoAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.CiRepos.AnyAsync(Ct));
    }

    /// <summary>GitHub stub: records scope probes and answers with a configurable error.</summary>
    private sealed class StubGitHubApiClient : GitHubApiClient {
        public string? AccessError { get; init; }
        public List<(string Owner, string Name)> Probes { get; } = [];

        public override Task<string?> ValidateRepoAccessAsync(
            string owner, string repo, string token, CancellationToken ct = default) {
            Probes.Add((owner, repo));
            return Task.FromResult(AccessError);
        }
    }
}
