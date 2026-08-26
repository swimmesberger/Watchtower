using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Ci.Handlers;
using Watchtower.Application.Modules.Products.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The operator-facing half of release-secret sync (<c>ci.setReleaseSecretsSync</c>): the up-front PAT
/// probe, the monorepo conflict reported in words before the filtered unique index has to enforce it,
/// the token minted so the sync has something to push, and the state cleared so the next pass
/// re-pushes (docs/products/design.md §"Secret sync").
/// </summary>
public sealed class CiReleaseSecretsSyncRpcTests {
    private const string RepoUrl = "https://github.com/acme/shop";

    private static Action<IServiceCollection> WithHandler(StubGitHubApiClient? gitHub = null) =>
        services => {
            services.AddSetReleaseSecretsSync();
            services.RemoveAll<GitHubApiClient>();
            services.AddSingleton<GitHubApiClient>(gitHub ?? new StubGitHubApiClient());
        };

    [Fact]
    public async Task Enable_ProbesThePat_AndArmsThePendingSync() {
        var gitHub = new StubGitHubApiClient();
        using var host = AuthTestHost.Start(WithHandler(gitHub));
        var ids = await SeedAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync(scope.ServiceProvider, new SetReleaseSecretsSync.Command(ids.ProductId, true));

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.True(result.Value.Ci.SyncReleaseSecrets);
        // Armed but not pushed: the orchestrator's next pass does that.
        Assert.Equal("pending", result.Value.Ci.ReleaseSecretsSync?.Status);
        Assert.Null(result.Value.Ci.ReleaseSecretsSyncBlocked);
        Assert.Equal([("acme", "shop")], gitHub.SecretsProbes);
    }

    [Fact]
    public async Task Enable_WithoutAToken_MintsOneAndEnablesTheWebhook() {
        using var host = AuthTestHost.Start(WithHandler());
        var ids = await SeedAsync(host, withToken: false);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync(scope.ServiceProvider, new SetReleaseSecretsSync.Command(ids.ProductId, true));
        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == ids.ProductId, Ct);
        Assert.NotNull(product.ReleaseWebhookToken);
        Assert.True(product.ReleaseWebhookEnabled);
    }

    [Fact]
    public async Task Enable_WhenAnotherProductOfTheRepoAlreadySyncs_IsRefusedNamingIt() {
        using var host = AuthTestHost.Start(WithHandler());
        var ids = await SeedAsync(host);
        var secondId = await AddSecondProductAsync(host, ids.RepoId, syncing: true);
        Assert.True(secondId > 0);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync(scope.ServiceProvider, new SetReleaseSecretsSync.Command(ids.ProductId, true));

        Assert.False(result.IsSuccess);
        Assert.Contains("shop-admin", result.Error.Message);
        Assert.Contains("only one product per repository", result.Error.Message);
        // And nothing was written — the refusal is complete, not partial.
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.Products.AsNoTracking()
            .Where(p => p.Id == ids.ProductId).Select(p => p.SyncReleaseSecrets).SingleAsync(Ct));
    }

    [Fact]
    public async Task Enable_WithAPatThatCannotWriteSecrets_IsRefusedAndPointsAtTheManualPath() {
        // The production string, and the release feature's wording specifically — the point of
        // parameterizing it is that this path stops telling operators about "the registry sync".
        var expected = GitHubApiClient.MissingActionsPermissionMessage(
            CiActionsConfigSync.ReleaseFeature, "Secrets");
        var gitHub = new StubGitHubApiClient { SecretsAccessError = expected };
        using var host = AuthTestHost.Start(WithHandler(gitHub));
        var ids = await SeedAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync(scope.ServiceProvider, new SetReleaseSecretsSync.Command(ids.ProductId, true));

        Assert.False(result.IsSuccess);
        Assert.Contains(expected, result.Error.Message);
        Assert.Contains("the release secret sync needs", result.Error.Message);
        Assert.DoesNotContain("registry sync", result.Error.Message);
        Assert.Equal([CiActionsConfigSync.ReleaseFeature], gitHub.Features);
        // The hobby guarantee: a wall is never the answer, the manual path is named in the same breath.
        Assert.Contains("by hand", result.Error.Message);
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.Products.AsNoTracking()
            .Where(p => p.Id == ids.ProductId).Select(p => p.SyncReleaseSecrets).SingleAsync(Ct));
    }

    [Fact]
    public async Task Enable_WithoutCiRunners_IsRefusedAndSaysWhatToDoFirst() {
        using var host = AuthTestHost.Start(WithHandler());
        var productId = await AddProductWithoutCiAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync(scope.ServiceProvider, new SetReleaseSecretsSync.Command(productId, true));

        Assert.False(result.IsSuccess);
        Assert.Contains("CI runners are not enabled", result.Error.Message);
        Assert.Contains("by hand", result.Error.Message);
    }

    [Fact]
    public async Task Enable_OfANonGitHubRemote_IsRefused() {
        using var host = AuthTestHost.Start(WithHandler());
        int productId;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var product = TestProducts.New("gitlab-thing", repositoryUrl: "https://gitlab.com/acme/shop.git");
            db.Products.Add(product);
            await db.SaveChangesAsync(Ct);
            productId = product.Id;
        }

        await using var outer = host.Services.CreateAsyncScope();
        var result = await SendAsync(outer.ServiceProvider, new SetReleaseSecretsSync.Command(productId, true));

        Assert.False(result.IsSuccess);
        Assert.Contains("does not deploy from a github.com repository", result.Error.Message);
        Assert.Contains("by hand", result.Error.Message);
    }

    [Fact]
    public async Task Disable_ClearsTheStateAndAuditsThatGitHubKeepsTheValues() {
        using var host = AuthTestHost.Start(WithHandler());
        var ids = await SeedAsync(host);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var enabled = await SendAsync(scope.ServiceProvider, new SetReleaseSecretsSync.Command(ids.ProductId, true));
            Assert.True(enabled.IsSuccess, enabled.IsSuccess ? null : enabled.Error.Message);
        }
        // A completed push, so there is state worth clearing.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var product = await db.Products.SingleAsync(p => p.Id == ids.ProductId, Ct);
            product.ActionsSyncedHash = "pushed";
            product.ActionsSyncedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync(scope.ServiceProvider, new SetReleaseSecretsSync.Command(ids.ProductId, false));

            Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
            Assert.False(result.Value.Ci.SyncReleaseSecrets);
            Assert.Null(result.Value.Ci.ReleaseSecretsSync);

            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == ids.ProductId, Ct);
            Assert.Null(product.ActionsSyncedHash);
            // The token survives: something in a workflow may still be presenting it.
            Assert.NotNull(product.ReleaseWebhookToken);
            var row = await db.AuditEvents.SingleAsync(
                e => e.Action == SetReleaseSecretsSync.AuditAction && e.Detail!.Contains("disabled"), Ct);
            Assert.Contains("left in place", row.Detail);
        }
    }

    [Fact]
    public async Task Enable_AfterAStandingFailure_ClearsItSoTheNextPassRetries() {
        using var host = AuthTestHost.Start(WithHandler());
        var ids = await SeedAsync(host);
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var product = await db.Products.SingleAsync(p => p.Id == ids.ProductId, Ct);
            product.SyncReleaseSecrets = true;
            product.LastActionsSyncError = CiActionsConfigSync.PublicBaseUrlMissing;
            product.ActionsSyncedHash = "stale";
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync(scope.ServiceProvider, new SetReleaseSecretsSync.Command(ids.ProductId, true));

            Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
            Assert.Equal("pending", result.Value.Ci.ReleaseSecretsSync?.Status);
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == ids.ProductId, Ct);
            Assert.Null(product.LastActionsSyncError);
            Assert.Null(product.ActionsSyncedHash);
        }
    }

    /// <summary>
    /// The stranded shape <c>ci.removeRepo</c>'s <c>SET NULL</c> used to leave: flag on, FK null. The
    /// filtered unique index cannot see it and neither could a <c>CiRepoId == repo.Id</c> conflict
    /// query, so the second product's enable used to be accepted and the two would then overwrite each
    /// other's token. The conflict check goes through the resolver's URL match for exactly this row.
    /// </summary>
    [Fact]
    public async Task Enable_WhenAStrandedProductWithNoCiRepoLinkAlreadySyncs_IsStillRefused() {
        using var host = AuthTestHost.Start(WithHandler());
        var ids = await SeedAsync(host);
        await AddSecondProductAsync(host, repoId: null, syncing: true);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync(scope.ServiceProvider, new SetReleaseSecretsSync.Command(ids.ProductId, true));

        Assert.False(result.IsSuccess);
        Assert.Contains("shop-admin", result.Error.Message);
        Assert.Contains("only one product per repository", result.Error.Message);
    }

    // ── ci.removeRepo: the root cause of the stranded state ──────────────────

    /// <summary>
    /// Removing the CI repo takes the PAT that did the pushing with it, so the sync it was doing has to
    /// stop being claimed. Without this clear the product keeps the flag while the FK goes null, which
    /// is both a lie on the CI tab and the state the unique index cannot constrain.
    /// </summary>
    [Fact]
    public async Task RemoveRepo_TurnsOffTheReleaseSecretSyncOfEveryProductOfThatRepo() {
        using var host = AuthTestHost.Start(services => {
            WithHandler()(services);
            services.AddRemoveRepo();
        });
        var ids = await SeedAsync(host);
        await using var scope = host.Services.CreateAsyncScope();
        var enabled = await SendAsync(scope.ServiceProvider, new SetReleaseSecretsSync.Command(ids.ProductId, true));
        Assert.True(enabled.IsSuccess, enabled.IsSuccess ? null : enabled.Error.Message);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var tracked = await db.Products.SingleAsync(p => p.Id == ids.ProductId, Ct);
        tracked.ActionsSyncedHash = "pushed";
        tracked.ActionsSyncedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(Ct);

        var removed = await scope.ServiceProvider
            .GetRequiredService<IHandler<RemoveRepo.Command, Result<RemoveRepo.Response>>>()
            .HandleAsync(new RemoveRepo.Command(ids.RepoId), Ct);
        Assert.True(removed.IsSuccess, removed.IsSuccess ? null : removed.Error.Message);

        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == ids.ProductId, Ct);
        Assert.False(product.SyncReleaseSecrets);
        Assert.Null(product.ActionsSyncedHash);
        Assert.Null(product.ActionsSyncedAt);
        Assert.Null(product.LastActionsSyncError);
        // The token survives — something in a workflow may still be presenting it.
        Assert.NotNull(product.ReleaseWebhookToken);

        var cleared = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == RemoveRepo.SyncClearedAction, Ct);
        Assert.Equal("acme/shop", cleared.Target);
        Assert.Contains("'shop'", cleared.Detail);
        Assert.Contains("left in place", cleared.Detail);
        var removal = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == "repo.remove", Ct);
        Assert.Contains("release secret sync turned off for 'shop'", removal.Detail);
    }

    /// <summary>
    /// The consequence chain the clear breaks: after removal the product no longer claims to be synced,
    /// so the Releases tab falls back to the manual instructions and a rotation stops promising a push
    /// nothing will make. Both are read off the same two fields the UI reads.
    /// </summary>
    [Fact]
    public async Task RemoveRepo_ThenRotate_StopsClaimingASyncThatCannotHappen() {
        using var host = AuthTestHost.Start(services => {
            WithHandler()(services);
            services.AddRemoveRepo();
        });
        var ids = await SeedAsync(host);
        await using var scope = host.Services.CreateAsyncScope();
        Assert.True((await SendAsync(scope.ServiceProvider,
            new SetReleaseSecretsSync.Command(ids.ProductId, true))).IsSuccess);
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var tracked = await db.Products.SingleAsync(p => p.Id == ids.ProductId, Ct);
        tracked.ActionsSyncedHash = "pushed";
        tracked.ActionsSyncedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(Ct);

        Assert.True((await scope.ServiceProvider
            .GetRequiredService<IHandler<RemoveRepo.Command, Result<RemoveRepo.Response>>>()
            .HandleAsync(new RemoveRepo.Command(ids.RepoId), Ct)).IsSuccess);

        // What the Releases tab reads: syncReleaseSecrets false and no state, so isSecretSyncLive is
        // false and the manual instructions are back.
        var ci = await ActivatorUtilities.CreateInstance<GetProductCi>(scope.ServiceProvider)
            .HandleAsync(new GetProductCi.Query(ids.ProductId), Ct);
        Assert.True(ci.IsSuccess, ci.IsSuccess ? null : ci.Error.Message);
        Assert.False(ci.Value.Ci.SyncReleaseSecrets);
        Assert.Null(ci.Value.Ci.ReleaseSecretsSync);
        Assert.Contains("Enable CI runners", ci.Value.Ci.ReleaseSecretsSyncBlocked);

        // …and the rotation's promise follows the same field, so it no longer says "on its way".
        var rotated = await ActivatorUtilities.CreateInstance<RotateReleaseToken>(scope.ServiceProvider)
            .HandleAsync(new RotateReleaseToken.Command(ids.ProductId), Ct);
        Assert.True(rotated.IsSuccess, rotated.IsSuccess ? null : rotated.Error.Message);
        Assert.False(rotated.Value.Resyncing);
    }

    // ── The other two triggers: token rotation and the product save ──────────

    /// <summary>
    /// Rotating the token is the one change that <em>must</em> reach GitHub: the value in the
    /// repository stopped working the moment this returned.
    /// </summary>
    [Fact]
    public async Task RotateReleaseToken_OfASyncingProduct_ClearsTheHashAndSaysSoOnTheWire() {
        using var host = AuthTestHost.Start(WithHandler());
        var ids = await SeedAsync(host);
        await using var scope = host.Services.CreateAsyncScope();
        var enabled = await SendAsync(scope.ServiceProvider, new SetReleaseSecretsSync.Command(ids.ProductId, true));
        Assert.True(enabled.IsSuccess, enabled.IsSuccess ? null : enabled.Error.Message);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var tracked = await db.Products.SingleAsync(p => p.Id == ids.ProductId, Ct);
        tracked.ActionsSyncedHash = "pushed";
        tracked.ActionsSyncedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(Ct);

        var rotated = await ActivatorUtilities.CreateInstance<RotateReleaseToken>(scope.ServiceProvider)
            .HandleAsync(new RotateReleaseToken.Command(ids.ProductId), Ct);

        Assert.True(rotated.IsSuccess, rotated.IsSuccess ? null : rotated.Error.Message);
        Assert.True(rotated.Value.Resyncing);
        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == ids.ProductId, Ct);
        Assert.Null(product.ActionsSyncedHash);
        Assert.Null(product.ActionsSyncedAt);
        var row = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == RotateReleaseToken.AuditAction, Ct);
        Assert.Contains("queued for re-sync", row.Detail);
    }

    /// <summary>
    /// A product that does not sync says so, and nothing about the sync state is touched — the
    /// rotation stays the pure token operation it was before this stage.
    /// </summary>
    [Fact]
    public async Task RotateReleaseToken_OfANonSyncingProduct_ReportsNoResync() {
        using var host = AuthTestHost.Start(WithHandler());
        var ids = await SeedAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var rotated = await ActivatorUtilities.CreateInstance<RotateReleaseToken>(scope.ServiceProvider)
            .HandleAsync(new RotateReleaseToken.Command(ids.ProductId), Ct);

        Assert.True(rotated.IsSuccess, rotated.IsSuccess ? null : rotated.Error.Message);
        Assert.False(rotated.Value.Resyncing);
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == RotateReleaseToken.AuditAction, Ct);
        Assert.DoesNotContain("re-sync", row.Detail);
    }

    /// <summary>
    /// Moving the repository takes the sync with it: the token is sitting in a repository this product
    /// no longer names, and leaving the switch on would keep a "synced" badge over nothing.
    /// </summary>
    [Fact]
    public async Task UpdateProduct_ThatMovesTheRepository_TurnsTheSyncOff() {
        using var host = AuthTestHost.Start(WithHandler());
        var ids = await SeedAsync(host);
        await using var scope = host.Services.CreateAsyncScope();
        var enabled = await SendAsync(scope.ServiceProvider, new SetReleaseSecretsSync.Command(ids.ProductId, true));
        Assert.True(enabled.IsSuccess, enabled.IsSuccess ? null : enabled.Error.Message);

        var moved = await ActivatorUtilities.CreateInstance<UpdateProduct>(scope.ServiceProvider)
            .HandleAsync(new UpdateProduct.Command(
                ids.ProductId, "shop", "https://github.com/acme/shop-moved", TestProducts.ComposeFilePath,
                TestProducts.DefaultBranch), Ct);

        Assert.True(moved.IsSuccess, moved.IsSuccess ? null : moved.Error.Message);
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == ids.ProductId, Ct);
        Assert.False(product.SyncReleaseSecrets);
        Assert.Null(product.ActionsSyncedHash);
        Assert.Null(product.CiRepoId);
        var row = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == "product.update", Ct);
        Assert.Contains("release secret sync turned off", row.Detail);
    }

    /// <summary>
    /// Any other save is the operator saying "I fixed the thing the error named" — the durable failures
    /// here are about the instance and the product, so a save clears them and asks for a retry.
    /// </summary>
    [Fact]
    public async Task UpdateProduct_WithAStandingSyncFailure_ClearsItSoTheNextPassRetries() {
        using var host = AuthTestHost.Start(WithHandler());
        var ids = await SeedAsync(host);
        await using var scope = host.Services.CreateAsyncScope();
        var enabled = await SendAsync(scope.ServiceProvider, new SetReleaseSecretsSync.Command(ids.ProductId, true));
        Assert.True(enabled.IsSuccess, enabled.IsSuccess ? null : enabled.Error.Message);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var tracked = await db.Products.SingleAsync(p => p.Id == ids.ProductId, Ct);
        tracked.ActionsSyncedHash = "pushed";
        tracked.LastActionsSyncError = CiActionsConfigSync.PublicBaseUrlMissing;
        await db.SaveChangesAsync(Ct);

        var renamed = await ActivatorUtilities.CreateInstance<UpdateProduct>(scope.ServiceProvider)
            .HandleAsync(new UpdateProduct.Command(
                ids.ProductId, "shop-renamed", RepoUrl, TestProducts.ComposeFilePath,
                TestProducts.DefaultBranch), Ct);

        Assert.True(renamed.IsSuccess, renamed.IsSuccess ? null : renamed.Error.Message);
        var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == ids.ProductId, Ct);
        Assert.True(product.SyncReleaseSecrets);
        Assert.Null(product.LastActionsSyncError);
        // The hash goes with it, or the "unchanged, nothing to do" guard would swallow the retry.
        Assert.Null(product.ActionsSyncedHash);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ValueTask<Result<SetReleaseSecretsSync.Response>> SendAsync(
        IServiceProvider scope, SetReleaseSecretsSync.Command command) =>
        scope.GetRequiredService<IHandler<SetReleaseSecretsSync.Command, Result<SetReleaseSecretsSync.Response>>>()
            .HandleAsync(command, Ct);

    private sealed record Ids(int ProductId, int RepoId);

    private static async Task<Ids> SeedAsync(AuthTestHost host, bool withToken = true) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var credential = new Credential {
            Name = "runner-admin", Username = "x-access-token", Token = "pat",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Credentials.Add(credential);
        var repo = new CiRepo {
            Owner = "acme", Name = "shop", Credential = credential, Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CiRepos.Add(repo);
        var product = new Product {
            Name = "shop",
            RepositoryUrl = RepoUrl,
            ComposeFilePath = TestProducts.ComposeFilePath,
            DefaultBranch = TestProducts.DefaultBranch,
            CiRepo = repo,
            ReleaseWebhookToken = withToken ? ReleaseWebhookTokens.Generate() : null,
            ReleaseWebhookEnabled = withToken,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(Ct);
        return new Ids(product.Id, repo.Id);
    }

    private static async Task<int> AddSecondProductAsync(AuthTestHost host, int? repoId, bool syncing) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var product = new Product {
            Name = "shop-admin",
            RepositoryUrl = RepoUrl,
            ComposeFilePath = "admin/docker-compose.yml",
            DefaultBranch = TestProducts.DefaultBranch,
            CiRepoId = repoId,
            SyncReleaseSecrets = syncing,
            ReleaseWebhookToken = ReleaseWebhookTokens.Generate(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(Ct);
        return product.Id;
    }

    private static async Task<int> AddProductWithoutCiAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var product = TestProducts.New("no-ci", repositoryUrl: "https://github.com/acme/no-ci");
        db.Products.Add(product);
        await db.SaveChangesAsync(Ct);
        return product.Id;
    }

    /// <summary>GitHub stub: records secrets probes and answers with a configurable error.</summary>
    private sealed class StubGitHubApiClient : GitHubApiClient {
        public string? SecretsAccessError { get; init; }
        public List<(string Owner, string Name)> SecretsProbes { get; } = [];

        /// <summary>Feature name each probe was made under — what decides the message's wording.</summary>
        public List<string> Features { get; } = [];

        public override Task<string?> ValidateSecretsAccessAsync(
            string owner, string repo, string token, string feature, CancellationToken ct = default) {
            SecretsProbes.Add((owner, repo));
            Features.Add(feature);
            return Task.FromResult(SecretsAccessError);
        }
    }
}
