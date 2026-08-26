using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sodium;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The per-repo GitHub Actions config pass (<see cref="CiActionsConfigSync"/>): what the release
/// contributor pushes, when both contributors decline to push, and the two properties the whole
/// design rests on — that the contributors are <em>independent</em> (docs/products/design.md
/// §"Secret sync": "a registry credential rotation must not re-push the release token, and vice
/// versa") and that neither can take the other down.
/// </summary>
public sealed class CiActionsConfigSyncTests {
    private const string BaseUrl = "https://watchtower.example.test";
    private const string RepoUrl = "https://github.com/acme/shop";

    // Unique enough that no developer machine's ~/.docker/config.json collides with it.
    private const string RegistryUrl = "registry.watchtower-tests.internal";

    [Fact]
    public async Task Release_PushesTheUrlTheProductIdAndTheSealedToken() {
        var gitHub = new RecordingGitHubApiClient();
        using var host = Start(gitHub);
        var estate = await SeedAsync(host, syncReleaseSecrets: true);

        await SyncAsync(host, estate);

        Assert.Equal(BaseUrl, gitHub.Variables[CiActionsConfigSync.UrlVariable]);
        Assert.Equal(estate.ProductId.ToString(), gitHub.Variables[CiActionsConfigSync.ProductIdVariable]);
        // GitHub's secrets API accepts exactly libsodium sealed boxes — prove ours opens to the token.
        Assert.Equal(estate.Token, gitHub.Open(gitHub.Secrets[CiActionsConfigSync.TokenSecret]));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var product = await db.Products.SingleAsync(p => p.Id == estate.ProductId, Ct);
        Assert.NotNull(product.ActionsSyncedHash);
        Assert.NotNull(product.ActionsSyncedAt);
        Assert.Null(product.LastActionsSyncError);

        var row = await db.AuditEvents.SingleAsync(
            e => e.Category == "ci" && e.Action == CiActionsConfigSync.ReleaseAuditAction, Ct);
        Assert.Equal("acme/shop", row.Target);
        Assert.True(row.Success);
        Assert.Null(row.Actor);
    }

    [Fact]
    public async Task Release_WithNothingChanged_MakesNoGitHubCallAtAll() {
        var gitHub = new RecordingGitHubApiClient();
        using var host = Start(gitHub);
        var estate = await SeedAsync(host, syncReleaseSecrets: true);

        await SyncAsync(host, estate);
        var afterFirst = gitHub.CallCount;
        await SyncAsync(host, estate);

        Assert.Equal(afterFirst, gitHub.CallCount);
    }

    [Fact]
    public async Task Release_WhenTheTokenIsRotated_PushesAgain() {
        var gitHub = new RecordingGitHubApiClient();
        using var host = Start(gitHub);
        var estate = await SeedAsync(host, syncReleaseSecrets: true);
        await SyncAsync(host, estate);

        var rotated = ReleaseWebhookTokens.Generate();
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var product = await db.Products.SingleAsync(p => p.Id == estate.ProductId, Ct);
            product.ReleaseWebhookToken = rotated;
            await db.SaveChangesAsync(Ct);
        }

        await SyncAsync(host, estate);

        Assert.Equal(rotated, gitHub.Open(gitHub.Secrets[CiActionsConfigSync.TokenSecret]));
        Assert.Equal(2, gitHub.SecretWrites.Count(w => w == CiActionsConfigSync.TokenSecret));
    }

    [Fact]
    public async Task Contributors_AreIndependent_ARegistryRotationDoesNotRePushTheReleaseValues() {
        var gitHub = new RecordingGitHubApiClient();
        using var host = Start(gitHub);
        var estate = await SeedAsync(host, syncReleaseSecrets: true, withRegistrySync: true);
        await SyncAsync(host, estate);
        Assert.Single(gitHub.SecretWrites, w => w == CiActionsConfigSync.TokenSecret);

        // Rotate the registry credential and nothing else.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var credential = await db.Credentials.SingleAsync(c => c.Name == "push-internal", Ct);
            credential.Token = "rotated-push-token";
            await db.SaveChangesAsync(Ct);
        }

        await SyncAsync(host, estate);

        // The registry half re-pushed…
        Assert.Equal(2, gitHub.SecretWrites.Count(w => w == "REGISTRY_PASSWORD"));
        Assert.Equal("rotated-push-token", gitHub.Open(gitHub.Secrets["REGISTRY_PASSWORD"]));
        // …and the release half did not, because its own hash did not move.
        Assert.Single(gitHub.SecretWrites, w => w == CiActionsConfigSync.TokenSecret);
        Assert.Single(gitHub.VariableWrites, w => w == CiActionsConfigSync.UrlVariable);
    }

    [Fact]
    public async Task Contributors_AreIndependent_AReleaseRotationDoesNotRePushTheRegistryValues() {
        var gitHub = new RecordingGitHubApiClient();
        using var host = Start(gitHub);
        var estate = await SeedAsync(host, syncReleaseSecrets: true, withRegistrySync: true);
        await SyncAsync(host, estate);
        Assert.Single(gitHub.SecretWrites, w => w == "REGISTRY_PASSWORD");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var product = await db.Products.SingleAsync(p => p.Id == estate.ProductId, Ct);
            product.ReleaseWebhookToken = ReleaseWebhookTokens.Generate();
            await db.SaveChangesAsync(Ct);
        }

        await SyncAsync(host, estate);

        Assert.Equal(2, gitHub.SecretWrites.Count(w => w == CiActionsConfigSync.TokenSecret));
        Assert.Single(gitHub.SecretWrites, w => w == "REGISTRY_PASSWORD");
        Assert.Single(gitHub.VariableWrites, w => w == "REGISTRY");
    }

    [Fact]
    public async Task Release_WithNoPublicBaseUrl_RecordsADurableErrorAndPushesNothing() {
        var gitHub = new RecordingGitHubApiClient();
        using var host = Start(gitHub, publicBaseUrl: null);
        var estate = await SeedAsync(host, syncReleaseSecrets: true);

        await SyncAsync(host, estate);

        Assert.Empty(gitHub.SecretWrites);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var product = await db.Products.SingleAsync(p => p.Id == estate.ProductId, Ct);
        Assert.Equal(CiActionsConfigSync.PublicBaseUrlMissing, product.LastActionsSyncError);
        Assert.Contains("Watchtower:PublicBaseUrl", product.LastActionsSyncError);
        Assert.Null(product.ActionsSyncedHash);
    }

    /// <summary>
    /// A local, permanent-until-an-operator-acts state records the durable error but must not arm the
    /// shared defer: no GitHub round-trip was spent, and the timer is the registry contributor's too —
    /// parking it for five minutes over an unset base URL would slow a credential rotation that has
    /// nothing to do with releases.
    /// </summary>
    [Fact]
    public async Task LocalFailures_DoNotArmTheSharedDefer_SoTheRegistryContributorKeepsItsLatency() {
        var gitHub = new RecordingGitHubApiClient();
        using var host = Start(gitHub, publicBaseUrl: null);
        var estate = await SeedAsync(host, syncReleaseSecrets: true, withRegistrySync: true);

        var status = new CiRepoRunnerStatus();
        await SyncAsync(host, estate, status);

        Assert.Null(status.ActionsSyncRetryAt);
        // Proof it matters: the registry contributor is still free to push on the very same pass.
        Assert.Contains("REGISTRY_PASSWORD", gitHub.SecretWrites);
    }

    /// <summary>The other side of the rule: a failed GitHub call is exactly what the defer is for.</summary>
    [Fact]
    public async Task AGitHubFailure_DoesArmTheSharedDefer() {
        var gitHub = new RecordingGitHubApiClient {
            SecretHttpError = new HttpRequestException("GitHub API 403 Forbidden: no write access"),
        };
        using var host = Start(gitHub);
        var estate = await SeedAsync(host, syncReleaseSecrets: true);

        var status = new CiRepoRunnerStatus();
        await SyncAsync(host, estate, status);

        Assert.NotNull(status.ActionsSyncRetryAt);
    }

    [Fact]
    public async Task Release_OfAProductThatDoesNotSync_IsNotPushed() {
        var gitHub = new RecordingGitHubApiClient();
        using var host = Start(gitHub);
        var estate = await SeedAsync(host, syncReleaseSecrets: false);

        await SyncAsync(host, estate);

        Assert.Empty(gitHub.SecretWrites);
        Assert.Empty(gitHub.VariableWrites);
    }

    [Fact]
    public async Task FailureIsolation_AThrowingReleaseContributor_LeavesTheRegistrySyncDone() {
        // The release contributor's very first call throws something that is not an HttpRequestException,
        // so it escapes its own error handling — exactly the case the per-contributor catch exists for.
        var gitHub = new RecordingGitHubApiClient { ThrowOnSecret = CiActionsConfigSync.TokenSecret };
        using var host = Start(gitHub);
        var estate = await SeedAsync(host, syncReleaseSecrets: true, withRegistrySync: true);

        await SyncAsync(host, estate);

        // The registry contributor ran first and completed…
        Assert.Contains("REGISTRY_USERNAME", gitHub.SecretWrites);
        Assert.Contains("REGISTRY_PASSWORD", gitHub.SecretWrites);
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var repo = await db.CiRepos.SingleAsync(r => r.Id == estate.RepoId, Ct);
        Assert.NotNull(repo.RegistrySyncedHash);
        Assert.Null(repo.LastRegistrySyncError);
        // …and the release contributor's blow-up did not reach the caller, which is the reconcile loop.
        var product = await db.Products.SingleAsync(p => p.Id == estate.ProductId, Ct);
        Assert.Null(product.ActionsSyncedHash);
    }

    [Fact]
    public async Task FailureIsolation_AThrowingRegistryContributor_StillRunsTheReleaseOne() {
        var gitHub = new RecordingGitHubApiClient { ThrowOnSecret = "REGISTRY_USERNAME" };
        using var host = Start(gitHub);
        var estate = await SeedAsync(host, syncReleaseSecrets: true, withRegistrySync: true);

        await SyncAsync(host, estate);

        Assert.Equal(estate.Token, gitHub.Open(gitHub.Secrets[CiActionsConfigSync.TokenSecret]));
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var product = await db.Products.SingleAsync(p => p.Id == estate.ProductId, Ct);
        Assert.NotNull(product.ActionsSyncedHash);
    }

    [Fact]
    public async Task Release_ThatKeepsFailingTheSameWay_AuditsTheTransitionOnly() {
        var gitHub = new RecordingGitHubApiClient {
            SecretHttpError = new HttpRequestException("GitHub API 403 Forbidden: no write access"),
        };
        using var host = Start(gitHub);
        var estate = await SeedAsync(host, syncReleaseSecrets: true);

        // Three passes; the defer is cleared between them so each one actually reaches GitHub.
        var status = new CiRepoRunnerStatus();
        await SyncAsync(host, estate, status);
        await SyncAsync(host, estate, status: null);
        await SyncAsync(host, estate, status: null);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var rows = await db.AuditEvents
            .Where(e => e.Category == "ci" && e.Action == CiActionsConfigSync.ReleaseAuditAction)
            .ToListAsync(Ct);
        Assert.Single(rows);
        Assert.False(rows[0].Success);
        Assert.Contains("Secrets (read and write)", rows[0].Error);
        var product = await db.Products.SingleAsync(p => p.Id == estate.ProductId, Ct);
        Assert.Contains("Secrets (read and write)", product.LastActionsSyncError);
        // The defer is set, so a pass reusing the same status would have skipped GitHub entirely.
        Assert.NotNull(status.ActionsSyncRetryAt);
    }

    [Fact]
    public async Task Release_AfterAFailure_RePushesEvenThoughTheValuesDidNotChange() {
        var gitHub = new RecordingGitHubApiClient();
        using var host = Start(gitHub);
        var estate = await SeedAsync(host, syncReleaseSecrets: true);
        await SyncAsync(host, estate);

        // A standing error with a matching hash: the guard has to fall through it, or a transient
        // GitHub failure would leave the product stuck reporting "failed" forever.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var product = await db.Products.SingleAsync(p => p.Id == estate.ProductId, Ct);
            product.LastActionsSyncError = "GitHub API 502 Bad Gateway";
            await db.SaveChangesAsync(Ct);
        }

        await SyncAsync(host, estate);

        Assert.Equal(2, gitHub.SecretWrites.Count(w => w == CiActionsConfigSync.TokenSecret));
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var product = await db.Products.SingleAsync(p => p.Id == estate.ProductId, Ct);
            Assert.Null(product.LastActionsSyncError);
        }
    }

    /// <summary>
    /// The stranded state the <c>SET NULL</c> foreign key used to leave behind: the flag is on, the FK
    /// is null, and the filtered unique index cannot see the row. The pass must still find it by the
    /// parsed URL — a sync that only worked while the cached FK happened to be filled in would be a
    /// silent no-op wearing a "synced" badge.
    /// </summary>
    [Fact]
    public async Task Release_OfAProductWhoseCiRepoLinkIsNull_IsStillFoundByTheParsedUrl() {
        var gitHub = new RecordingGitHubApiClient();
        using var host = Start(gitHub);
        var estate = await SeedAsync(host, syncReleaseSecrets: true);
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.Products.Where(p => p.Id == estate.ProductId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CiRepoId, (int?)null), Ct);
        }

        await SyncAsync(host, estate);

        Assert.Equal(estate.Token, gitHub.Open(gitHub.Secrets[CiActionsConfigSync.TokenSecret]));
        Assert.Equal(estate.ProductId.ToString(), gitHub.Variables[CiActionsConfigSync.ProductIdVariable]);
    }

    /// <summary>
    /// …and two of them is the dangerous half of that same gap: with one FK null the index constrains
    /// nothing, and picking the lowest id would push one product's token into the repository the other
    /// was wired for. Neither is synced, and both are told why.
    /// </summary>
    [Fact]
    public async Task Release_WithTwoSyncingProductsOfOneRepo_SyncsNeitherAndRecordsTheConflictOnBoth() {
        var gitHub = new RecordingGitHubApiClient();
        using var host = Start(gitHub);
        var estate = await SeedAsync(host, syncReleaseSecrets: true);
        int strandedId;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            // The exact shape ci.removeRepo used to leave: flag on, FK null.
            var stranded = new Product {
                Name = "shop-admin",
                RepositoryUrl = RepoUrl,
                ComposeFilePath = "admin/docker-compose.yml",
                DefaultBranch = TestProducts.DefaultBranch,
                CiRepoId = null,
                SyncReleaseSecrets = true,
                ReleaseWebhookToken = ReleaseWebhookTokens.Generate(),
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Products.Add(stranded);
            await db.SaveChangesAsync(Ct);
            strandedId = stranded.Id;
        }

        await SyncAsync(host, estate);

        Assert.Empty(gitHub.SecretWrites);
        Assert.Empty(gitHub.VariableWrites);
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            foreach (var id in new[] { estate.ProductId, strandedId }) {
                var product = await db.Products.AsNoTracking().SingleAsync(p => p.Id == id, Ct);
                Assert.Contains("'shop'", product.LastActionsSyncError);
                Assert.Contains("'shop-admin'", product.LastActionsSyncError);
                Assert.Contains("Nothing was pushed", product.LastActionsSyncError);
                Assert.Null(product.ActionsSyncedHash);
            }
        }
    }

    [Fact]
    public async Task MonorepoRule_ASecondSyncingProductOfTheSameRepo_IsRefusedByTheIndex() {
        using var host = Start(new RecordingGitHubApiClient());
        var estate = await SeedAsync(host, syncReleaseSecrets: true);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.Products.Add(new Product {
            Name = "shop-admin",
            RepositoryUrl = RepoUrl,
            ComposeFilePath = "admin/docker-compose.yml",
            DefaultBranch = "main",
            CiRepoId = estate.RepoId,
            SyncReleaseSecrets = true,
            ReleaseWebhookToken = ReleaseWebhookTokens.Generate(),
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.Contains("ix_products_ci_repo_id_sync_release_secrets", ex.InnerException?.Message ?? "");
    }

    [Fact]
    public async Task MonorepoRule_ASecondProductOfTheSameRepoThatDoesNotSync_IsFine() {
        using var host = Start(new RecordingGitHubApiClient());
        var estate = await SeedAsync(host, syncReleaseSecrets: true);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.Products.Add(new Product {
            Name = "shop-admin",
            RepositoryUrl = RepoUrl,
            ComposeFilePath = "admin/docker-compose.yml",
            DefaultBranch = "main",
            CiRepoId = estate.RepoId,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync(Ct);
        Assert.Equal(2, await db.Products.CountAsync(p => p.CiRepoId == estate.RepoId, Ct));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthTestHost Start(RecordingGitHubApiClient gitHub, string? publicBaseUrl = BaseUrl) =>
        AuthTestHost.Start(
            services => {
                services.RemoveAll<GitHubApiClient>();
                services.AddSingleton<GitHubApiClient>(gitHub);
            },
            ("Watchtower:PublicBaseUrl", publicBaseUrl));

    private sealed record Estate(int ProductId, int RepoId, string Token);

    /// <summary>
    /// One CI repo for <c>acme/shop</c> with a PAT, one product deploying it, and — when asked — a
    /// Watchtower registry the repo syncs, so both contributors have something to do.
    /// </summary>
    private static async Task<Estate> SeedAsync(
        AuthTestHost host, bool syncReleaseSecrets, bool withRegistrySync = false) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        var pat = new Credential {
            Name = "runner-admin", Username = "x-access-token", Token = "pat",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Credentials.Add(pat);
        var repo = new CiRepo {
            Owner = "acme", Name = "shop", Credential = pat, Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CiRepos.Add(repo);

        if (withRegistrySync) {
            var push = new Credential {
                Name = "push-internal", Username = "pusher", Token = "push-token",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Credentials.Add(push);
            db.Registries.Add(new Registry {
                Name = "internal", Url = RegistryUrl, Credential = push, CreatedAt = DateTimeOffset.UtcNow,
            });
            repo.SyncRegistryUrl = RegistryUrl;
        }

        var token = ReleaseWebhookTokens.Generate();
        var product = new Product {
            Name = "shop",
            RepositoryUrl = RepoUrl,
            ComposeFilePath = TestProducts.ComposeFilePath,
            DefaultBranch = TestProducts.DefaultBranch,
            CiRepo = repo,
            ReleaseWebhookToken = token,
            ReleaseWebhookEnabled = true,
            SyncReleaseSecrets = syncReleaseSecrets,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(Ct);
        return new Estate(product.Id, repo.Id, token);
    }

    /// <summary>
    /// Runs one pass the way the orchestrator runs it: the repo re-read with its credential, and a
    /// per-repo status object. A fresh status per call unless one is handed in, because the shared
    /// five-minute defer would otherwise silence every pass after a failure.
    /// </summary>
    private static async Task SyncAsync(AuthTestHost host, Estate estate, CiRepoRunnerStatus? status = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var repo = await db.CiRepos.AsNoTracking().Include(r => r.Credential)
            .SingleAsync(r => r.Id == estate.RepoId, Ct);
        await host.Services.GetRequiredService<CiActionsConfigSync>()
            .SyncActionsConfigAsync(repo, status ?? new CiRepoRunnerStatus(), Ct);
    }

    /// <summary>
    /// A GitHub that records instead of calling: one real libsodium keypair so sealed values can be
    /// opened and asserted on, plus two independent failure injections — an
    /// <see cref="HttpRequestException"/> (what GitHub's own errors look like, handled by the
    /// contributors) and a bare throw (what the per-contributor isolation exists for).
    /// </summary>
    private sealed class RecordingGitHubApiClient : GitHubApiClient {
        private readonly KeyPair _keys = PublicKeyBox.GenerateKeyPair();

        public Dictionary<string, string> Secrets { get; } = [];
        public Dictionary<string, string> Variables { get; } = [];
        public List<string> SecretWrites { get; } = [];
        public List<string> VariableWrites { get; } = [];
        public int CallCount { get; private set; }

        /// <summary>Secret name whose write throws a plain exception (escapes the contributor's own handling).</summary>
        public string? ThrowOnSecret { get; init; }

        /// <summary>Error every secret write answers with, as GitHub would.</summary>
        public HttpRequestException? SecretHttpError { get; init; }

        public string Open(string sealedValue) => Encoding.UTF8.GetString(
            SealedPublicKeyBox.Open(Convert.FromBase64String(sealedValue), _keys.PrivateKey, _keys.PublicKey));

        public override Task<GitHubActionsPublicKey> GetActionsPublicKeyAsync(
            string owner, string repo, string token, CancellationToken ct = default) {
            CallCount++;
            return Task.FromResult(new GitHubActionsPublicKey {
                KeyId = "key-1", Key = Convert.ToBase64String(_keys.PublicKey),
            });
        }

        public override Task PutActionsSecretAsync(
            string owner, string repo, string name, string encryptedValue, string keyId, string token,
            CancellationToken ct = default) {
            CallCount++;
            if (name == ThrowOnSecret)
                throw new InvalidOperationException($"stub blew up writing {name}");
            if (SecretHttpError is not null)
                throw SecretHttpError;
            SecretWrites.Add(name);
            Secrets[name] = encryptedValue;
            return Task.CompletedTask;
        }

        public override Task SetActionsVariableAsync(
            string owner, string repo, string name, string value, string token, CancellationToken ct = default) {
            CallCount++;
            VariableWrites.Add(name);
            Variables[name] = value;
            return Task.CompletedTask;
        }
    }
}
