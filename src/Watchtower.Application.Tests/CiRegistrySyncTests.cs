using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Sodium;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Ci.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the registry→GitHub Actions sync selection (docs/ci-runners/design.md, Secrets §1):
/// the selection-time resolution guard in <c>ci.updateRepo</c>, the sync-state reset on a changed
/// selection, and the sealed-box encryption GitHub's secrets API requires.
/// </summary>
public sealed class CiRegistrySyncTests {
    // Unique enough that no developer machine's ~/.docker/config.json collides with it.
    private const string RegistryUrl = "registry.watchtower-tests.internal";

    private static Action<IServiceCollection> WithUpdateRepo => services => services.AddUpdateRepo();

    [Fact]
    public async Task Update_RejectsARegistryUrl_ThatResolvesNowhere() {
        using var host = AuthTestHost.Start(WithUpdateRepo);
        var repoId = await AddCiRepoAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync(scope.ServiceProvider, Command(repoId) with {
            SyncRegistryUrl = "registry.nowhere.invalid",
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("registry.nowhere.invalid", result.Error.Message);
    }

    [Fact]
    public async Task Update_SelectingAWatchtowerRegistry_StartsThePendingSync() {
        using var host = AuthTestHost.Start(WithUpdateRepo);
        var repoId = await AddCiRepoAsync(host);
        await AddRegistryAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync(scope.ServiceProvider, Command(repoId) with {
            SyncRegistryUrl = RegistryUrl,
        });

        Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
        Assert.Equal(RegistryUrl, result.Value.Repo.SyncRegistryUrl);
        // Selected but not pushed yet — the orchestrator's next pass does that.
        Assert.Equal("pending", result.Value.Repo.RegistrySync?.Status);
    }

    [Fact]
    public async Task Update_ChangingTheSelection_DropsTheSyncedState() {
        using var host = AuthTestHost.Start(WithUpdateRepo);
        var repoId = await AddCiRepoAsync(host);
        await AddRegistryAsync(host);
        await AddRegistryAsync(host, name: "other", url: $"other.{RegistryUrl}");

        // Simulate a completed sync of the first registry.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var repo = await db.CiRepos.SingleAsync(r => r.Id == repoId, Ct);
            repo.SyncRegistryUrl = RegistryUrl;
            repo.RegistrySyncedHash = "stale-hash";
            repo.RegistrySyncedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync(scope.ServiceProvider, Command(repoId) with {
                SyncRegistryUrl = $"other.{RegistryUrl}",
            });

            Assert.True(result.IsSuccess, result.IsSuccess ? null : result.Error.Message);
            Assert.Equal("pending", result.Value.Repo.RegistrySync?.Status);
            Assert.Null(result.Value.Repo.RegistrySync?.SyncedAt);
        }
    }

    [Fact]
    public async Task Update_ClearingTheSelection_TurnsTheSyncOff() {
        using var host = AuthTestHost.Start(WithUpdateRepo);
        var repoId = await AddCiRepoAsync(host);
        await AddRegistryAsync(host);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var first = await SendAsync(scope.ServiceProvider, Command(repoId) with { SyncRegistryUrl = RegistryUrl });
            Assert.True(first.IsSuccess);
        }
        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync(scope.ServiceProvider, Command(repoId));

            Assert.True(result.IsSuccess);
            Assert.Null(result.Value.Repo.SyncRegistryUrl);
            Assert.Null(result.Value.Repo.RegistrySync);
        }
    }

    [Fact]
    public void SealedValues_OpenWithTheRecipientsKeyPair() {
        // GitHub's secrets API accepts exactly libsodium sealed boxes — prove ours open.
        using var recipient = PublicKeyBox.GenerateKeyPair();
        var sealed_ = GitHubSecretSealer.Seal(Convert.ToBase64String(recipient.PublicKey), "s3cret-value");

        var opened = SealedPublicKeyBox.Open(Convert.FromBase64String(sealed_), recipient.PrivateKey, recipient.PublicKey);
        Assert.Equal("s3cret-value", System.Text.Encoding.UTF8.GetString(opened));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UpdateRepo.Command Command(int repoId) => new(
        Id: repoId, Enabled: true, MaxConcurrentRunners: 1, CredentialId: 0,
        RunnerImage: null, ExtraLabels: null, AllowDockerSocket: false);

    private static async ValueTask<Result<UpdateRepo.Response>> SendAsync(
        IServiceProvider scope, UpdateRepo.Command command) {
        // The helper-built command carries CredentialId 0 — swap in the repo's real credential.
        var db = scope.GetRequiredService<WatchtowerDbContext>();
        var credentialId = await db.CiRepos.Where(r => r.Id == command.Id)
            .Select(r => r.CredentialId).SingleAsync(Ct);
        return await scope.GetRequiredService<IHandler<UpdateRepo.Command, Result<UpdateRepo.Response>>>()
            .HandleAsync(command with { CredentialId = credentialId }, Ct);
    }

    private static async Task<int> AddCiRepoAsync(AuthTestHost host) {
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
        await db.SaveChangesAsync(Ct);
        return repo.Id;
    }

    private static async Task AddRegistryAsync(AuthTestHost host, string name = "internal", string url = RegistryUrl) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var credential = new Credential {
            Name = $"push-{name}", Username = "pusher", Token = "push-token",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Credentials.Add(credential);
        db.Registries.Add(new Registry {
            Name = name, Url = url, Credential = credential, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }
}
