using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The deploy-side half of the product↔CI link (ADR-0026 decision 7): a deploy hands the recorder the
/// stack's <em>product</em> id, and the profile lands on the CI repo that product links to. The
/// URL-parse fallback still covers products whose FK has never been resolved — and records it, so the
/// parse happens once rather than on every deploy.
/// </summary>
public sealed class CiToolchainRecorderTests : IDisposable {
    private const string RepoUrl = "https://github.com/acme/shop.git";

    private readonly string _cloneDir = Directory.CreateTempSubdirectory("watchtower-ci-record-").FullName;

    public CiToolchainRecorderTests() {
        File.WriteAllText(Path.Combine(_cloneDir, "Dockerfile"), "FROM alpine\n");
    }

    public void Dispose() {
        try { Directory.Delete(_cloneDir, recursive: true); } catch { /* best-effort */ }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Record_FollowsTheProductsCiRepoLink() {
        using var host = AuthTestHost.Start();
        var (productId, repoId) = await SeedAsync(host, RepoUrl, link: true);

        var summary = await host.Services.GetRequiredService<CiToolchainRecorder>()
            .TryRecordAsync(productId, _cloneDir, Ct);

        Assert.NotNull(summary);
        Assert.Contains("acme/shop", summary);
        Assert.True(await HasDockerfileProfileAsync(host, repoId));
    }

    [Fact]
    public async Task Record_FallsBackToParsingTheUrl_AndRecordsTheLinkItFound() {
        using var host = AuthTestHost.Start();
        var (productId, repoId) = await SeedAsync(host, RepoUrl, link: false);

        var summary = await host.Services.GetRequiredService<CiToolchainRecorder>()
            .TryRecordAsync(productId, _cloneDir, Ct);

        Assert.NotNull(summary);
        Assert.True(await HasDockerfileProfileAsync(host, repoId));
        // The deploy that had to parse the URL is also the one that stops the next deploy having to.
        Assert.Equal(repoId, await CiRepoIdAsync(host, productId));
    }

    [Fact]
    public async Task Record_IsANoOpForANonGitHubProduct() {
        using var host = AuthTestHost.Start();
        var (productId, repoId) = await SeedAsync(host, "https://gitea.local/acme/shop.git", link: false);

        Assert.Null(await host.Services.GetRequiredService<CiToolchainRecorder>()
            .TryRecordAsync(productId, _cloneDir, Ct));

        Assert.False(await HasDockerfileProfileAsync(host, repoId));
        Assert.Null(await CiRepoIdAsync(host, productId));
    }

    [Fact]
    public async Task Record_IsANoOpWhenTheProductHasNoCiRepo() {
        using var host = AuthTestHost.Start();
        int productId;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var product = TestProducts.New("shop", RepoUrl);
            db.Products.Add(product);
            await db.SaveChangesAsync(Ct);
            productId = product.Id;
        }

        Assert.Null(await host.Services.GetRequiredService<CiToolchainRecorder>()
            .TryRecordAsync(productId, _cloneDir, Ct));
    }

    /// <summary>A product and a CI repo for <c>acme/shop</c>, linked or left for the fallback to find.</summary>
    private static async Task<(int ProductId, int RepoId)> SeedAsync(
        AuthTestHost host, string repositoryUrl, bool link) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var credential = new Credential {
            Name = "bot", Username = "git", Token = "t", CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Credentials.Add(credential);
        await db.SaveChangesAsync(Ct);

        var repo = new CiRepo {
            Owner = "acme", Name = "shop", CredentialId = credential.Id, Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CiRepos.Add(repo);
        var product = TestProducts.New("shop", repositoryUrl, credentialId: credential.Id);
        db.Products.Add(product);
        await db.SaveChangesAsync(Ct);

        if (link) {
            product.CiRepoId = repo.Id;
            await db.SaveChangesAsync(Ct);
        }
        return (product.Id, repo.Id);
    }

    private static async Task<bool> HasDockerfileProfileAsync(AuthTestHost host, int repoId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var json = await db.CiRepos.AsNoTracking()
            .Where(r => r.Id == repoId).Select(r => r.ToolchainProfileJson).FirstAsync(Ct);
        return CiToolchainProfile.FromJson(json)?.HasDockerfile == true;
    }

    private static async Task<int?> CiRepoIdAsync(AuthTestHost host, int productId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Products.AsNoTracking()
            .Where(p => p.Id == productId).Select(p => p.CiRepoId).FirstAsync(Ct);
    }
}
