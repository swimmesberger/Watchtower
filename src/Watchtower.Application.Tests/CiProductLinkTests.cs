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
/// Covers the product↔CI link through the real generated handler pipelines (ADR-0026 decision 7): the
/// <see cref="Product.CiRepoId"/> FK that replaced URL string matching, its lazy resolution on read
/// paths, the up-front PAT scope probe with its named-permission error, credential fallback to the
/// product's clone credential, the invariant that products of the same repository converge on one
/// <see cref="CiRepo"/>, and the stack-scoped forwards kept for one release.
/// </summary>
public sealed class CiProductLinkTests {
    private const string RepoUrl = "https://github.com/acme/shop.git";

    /// <summary>All four link handlers plus a GitHub stub, registered the way the generated module does.</summary>
    private static Action<IServiceCollection> WithCiLink(StubGitHubApiClient gitHub) => services => {
        services.AddGetProductCi();
        services.AddEnableForProduct();
        // The stack-scoped pair forwards into the two above, so both halves have to be present.
        services.AddGetStackCi();
        services.AddEnableForStack();
        services.RemoveAll<GitHubApiClient>();
        services.AddSingleton<GitHubApiClient>(gitHub);
    };

    // ── ci.enableForProduct ──────────────────────────────────────────────────

    [Fact]
    public async Task Enable_CreatesTheCiRepo_UsingTheProductsCloneCredentialByDefault() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "deploy-bot");
        var productId = await AddProductAsync(host, "shop", RepoUrl, credentialId);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<EnableForProduct.Command, EnableForProduct.Response>(
            scope.ServiceProvider, new EnableForProduct.Command(productId));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Equal("acme", result.Value.Repo.Owner);
        Assert.Equal("shop", result.Value.Repo.Name);
        Assert.Equal(credentialId, result.Value.Repo.CredentialId);
        Assert.True(result.Value.Repo.Enabled);
        Assert.Null(result.Value.Repo.Toolchain); // fills on the next deploy's detection pass
        Assert.Equal([("acme", "shop")], gitHub.Probes);
        // Enabling records the link rather than leaving it to be re-derived on every read.
        Assert.Equal(result.Value.Repo.Id, await CiRepoIdAsync(host, productId));
    }

    [Fact]
    public async Task Enable_FailsNamingTheMissingPermission_WhenTheClonePatCannotManageRunners() {
        // The scope probe answers what GitHub answers for a Contents-only fine-grained PAT.
        var gitHub = new StubGitHubApiClient {
            AccessError = "The PAT lacks the repository Administration permission required to register runners.",
        };
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "clone-only");
        var productId = await AddProductAsync(host, "shop", RepoUrl, credentialId);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<EnableForProduct.Command, EnableForProduct.Response>(
            scope.ServiceProvider, new EnableForProduct.Command(productId));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        // The message must name the credential, the missing permission, and the way out.
        Assert.Contains("clone-only", result.Error.Message);
        Assert.Contains("Administration", result.Error.Message);
        Assert.Contains("Choose or create a credential", result.Error.Message);
        await AssertNoCiRepoAsync(host);
        Assert.Null(await CiRepoIdAsync(host, productId));
    }

    [Fact]
    public async Task Enable_FailsWhenTheProductHasNoCredentialAndNoneIsChosen() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var productId = await AddProductAsync(host, "shop", RepoUrl, credentialId: null);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<EnableForProduct.Command, EnableForProduct.Response>(
            scope.ServiceProvider, new EnableForProduct.Command(productId));

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
        var productId = await AddProductAsync(host, "shop", "https://gitea.local/acme/shop.git", credentialId);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<EnableForProduct.Command, EnableForProduct.Response>(
            scope.ServiceProvider, new EnableForProduct.Command(productId));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains("github.com", result.Error.Message);
    }

    [Fact]
    public async Task Enable_TwoProductsOfTheSameRepository_ShareOneCiRepo() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "bot");
        var stagingId = await AddProductAsync(host, "shop-staging", RepoUrl, credentialId);
        // Same repo, different URL spelling — the parsed owner/name is the identity.
        var prodId = await AddProductAsync(host, "shop-prod", "git@github.com:acme/shop.git", credentialId);

        int firstRepoId;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var first = await SendAsync<EnableForProduct.Command, EnableForProduct.Response>(
                scope.ServiceProvider, new EnableForProduct.Command(stagingId));
            Assert.True(first.IsSuccess);
            firstRepoId = first.Value.Repo.Id;
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var second = await SendAsync<EnableForProduct.Command, EnableForProduct.Response>(
                scope.ServiceProvider, new EnableForProduct.Command(prodId));
            Assert.True(second.IsSuccess);
            // Linking, not duplicating: one runner pool and one toolcache for the repository.
            Assert.Equal(firstRepoId, second.Value.Repo.Id);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.Equal(1, await db.CiRepos.CountAsync(Ct));
        }
        // Both products point at it — the FK is not unique for exactly this case.
        Assert.Equal(firstRepoId, await CiRepoIdAsync(host, stagingId));
        Assert.Equal(firstRepoId, await CiRepoIdAsync(host, prodId));
        // The second enable reuses the already-validated credential — no second probe.
        Assert.Equal([("acme", "shop")], gitHub.Probes);
    }

    [Fact]
    public async Task Enable_ReenablesADisabledRepoInsteadOfConflicting() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "bot");
        var productId = await AddProductAsync(host, "shop", RepoUrl, credentialId);
        await AddCiRepoAsync(host, credentialId, enabled: false);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<EnableForProduct.Command, EnableForProduct.Response>(
                scope.ServiceProvider, new EnableForProduct.Command(productId));
            Assert.True(result.IsSuccess);
            Assert.True(result.Value.Repo.Enabled);
        }
    }

    // ── ci.getProductCi ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetProductCi_ReportsParseResultAndLink() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "bot");
        var gitHubProductId = await AddProductAsync(host, "shop", RepoUrl, credentialId);
        var foreignProductId = await AddProductAsync(host, "legacy", "https://gitea.local/acme/legacy.git", credentialId);

        await using var scope = host.Services.CreateAsyncScope();

        var unlinked = await SendAsync<GetProductCi.Query, GetProductCi.Response>(
            scope.ServiceProvider, new GetProductCi.Query(gitHubProductId));
        Assert.True(unlinked.IsSuccess);
        Assert.True(unlinked.Value.Ci.IsGitHub);
        Assert.Equal(("acme", "shop"), (unlinked.Value.Ci.Owner, unlinked.Value.Ci.Name));
        Assert.Null(unlinked.Value.Ci.Repo);

        // A non-GitHub remote is a clean answer, not an error: nothing parsed, nothing linked.
        var foreign = await SendAsync<GetProductCi.Query, GetProductCi.Response>(
            scope.ServiceProvider, new GetProductCi.Query(foreignProductId));
        Assert.True(foreign.IsSuccess);
        Assert.False(foreign.Value.Ci.IsGitHub);
        Assert.Null(foreign.Value.Ci.Owner);
        Assert.Null(foreign.Value.Ci.Repo);
        Assert.Null(await CiRepoIdAsync(host, foreignProductId));

        var enabled = await SendAsync<EnableForProduct.Command, EnableForProduct.Response>(
            scope.ServiceProvider, new EnableForProduct.Command(gitHubProductId));
        Assert.True(enabled.IsSuccess);

        var linked = await SendAsync<GetProductCi.Query, GetProductCi.Response>(
            scope.ServiceProvider, new GetProductCi.Query(gitHubProductId));
        Assert.True(linked.IsSuccess);
        Assert.Equal(enabled.Value.Repo.Id, linked.Value.Ci.Repo?.Id);
    }

    [Fact]
    public async Task GetProductCi_ResolvesAndRecordsTheLinkForAProductThatPredatesTheFk() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "bot");
        // The shape the backfill migration leaves behind: a CI repo already configured, and a product
        // whose ci_repo_id is null because parsing URLs in SQL was not worth it.
        var productId = await AddProductAsync(host, "shop", RepoUrl, credentialId);
        var repoId = await AddCiRepoAsync(host, credentialId, enabled: true);
        Assert.Null(await CiRepoIdAsync(host, productId));

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<GetProductCi.Query, GetProductCi.Response>(
                scope.ServiceProvider, new GetProductCi.Query(productId));
            Assert.True(result.IsSuccess);
            Assert.Equal(repoId, result.Value.Ci.Repo?.Id);
        }

        // Resolved once, then recorded: the next read follows the FK instead of parsing again.
        Assert.Equal(repoId, await CiRepoIdAsync(host, productId));
    }

    [Fact]
    public async Task GetProductCi_SurfacesTheDetectedToolchainProfile() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "bot");
        var productId = await AddProductAsync(host, "shop", RepoUrl, credentialId);
        var profile = new CiToolchainProfile {
            Toolchains = [new CiToolchain("dotnet", "10.0", "workflow")], HasDockerfile = true,
        };
        await AddCiRepoAsync(host, credentialId, enabled: true, profile: profile);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<GetProductCi.Query, GetProductCi.Response>(
                scope.ServiceProvider, new GetProductCi.Query(productId));

            Assert.True(result.IsSuccess);
            var toolchain = result.Value.Ci.Repo?.Toolchain;
            Assert.NotNull(toolchain);
            Assert.Equal([new CiToolchainDto("dotnet", "10.0", "workflow")], toolchain.Toolchains);
            Assert.True(toolchain.HasDockerfile);
            // Detected but never warmed → the UI shows the warm as outstanding, not failed.
            Assert.Equal("pending", toolchain.WarmStatus);
        }
    }

    // ── The stack-scoped forwards ────────────────────────────────────────────

    [Fact]
    public async Task StackScopedCalls_ForwardToTheStacksProduct() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));
        var credentialId = await AddCredentialAsync(host, "bot");
        var productId = await AddProductAsync(host, "shop", RepoUrl, credentialId);
        var stackId = await AddStackAsync(host, "shop-prod", productId);

        await using var scope = host.Services.CreateAsyncScope();

        var enabled = await SendAsync<EnableForStack.Command, EnableForStack.Response>(
            scope.ServiceProvider, new EnableForStack.Command(stackId));
        Assert.True(enabled.IsSuccess, enabled.IsSuccess ? null : enabled.Error.Message);
        Assert.Equal("acme/shop", enabled.Value.Repo.FullName);
        // The forward went through the product: the FK it wrote is the proof.
        Assert.Equal(enabled.Value.Repo.Id, await CiRepoIdAsync(host, productId));

        var read = await SendAsync<GetStackCi.Query, GetStackCi.Response>(
            scope.ServiceProvider, new GetStackCi.Query(stackId));
        Assert.True(read.IsSuccess);
        Assert.True(read.Value.Ci.IsGitHub);
        Assert.Equal(enabled.Value.Repo.Id, read.Value.Ci.Repo?.Id);
    }

    [Fact]
    public async Task StackScopedCalls_StillReportAMissingStackRatherThanAMissingProduct() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithCiLink(gitHub));

        await using var scope = host.Services.CreateAsyncScope();
        var read = await SendAsync<GetStackCi.Query, GetStackCi.Response>(
            scope.ServiceProvider, new GetStackCi.Query(4242));

        Assert.False(read.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, read.Error.Kind);
        Assert.Contains("Stack 4242", read.Error.Message);
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

    private static async Task<int> AddProductAsync(
        AuthTestHost host, string name, string repositoryUrl, int? credentialId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var product = TestProducts.New(name, repositoryUrl, credentialId: credentialId);
        db.Products.Add(product);
        await db.SaveChangesAsync(Ct);
        return product.Id;
    }

    private static async Task<int> AddStackAsync(AuthTestHost host, string name, int productId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = name, ComposeProjectName = name, ProductId = productId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);
        return stack.Id;
    }

    private static async Task<int> AddCiRepoAsync(
        AuthTestHost host, int credentialId, bool enabled, CiToolchainProfile? profile = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var repo = new CiRepo {
            Owner = "acme", Name = "shop", CredentialId = credentialId, Enabled = enabled,
            ToolchainProfileJson = profile?.ToJson(),
            ToolchainDetectedAt = profile is null ? null : DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CiRepos.Add(repo);
        await db.SaveChangesAsync(Ct);
        return repo.Id;
    }

    private static async Task<int?> CiRepoIdAsync(AuthTestHost host, int productId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Products.AsNoTracking()
            .Where(p => p.Id == productId).Select(p => p.CiRepoId).FirstAsync(Ct);
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
