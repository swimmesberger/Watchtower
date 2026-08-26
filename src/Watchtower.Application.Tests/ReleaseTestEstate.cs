using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Seeding helpers for the release-aware deploy tests: products in either mode, releases with images,
/// pins, and the deployed-release marker.
/// </summary>
/// <remarks>
/// Rows are written directly because they are the <em>preconditions</em> of the code under test.
/// <see cref="ReleaseIntakeTests"/> is what asserts about the code that records releases for real; here
/// a release is simply a fact the deploy pipeline has to act on.
/// </remarks>
internal static class ReleaseTestEstate {
    /// <summary>A plausible 40-hex commit, distinct from <see cref="StubGitCloneService.HeadCommit"/>.</summary>
    public const string ReleaseCommit = "abcdef0123456789abcdef0123456789abcdef01";

    /// <summary>A second one, for "the next release moved the commit".</summary>
    public const string NextReleaseCommit = "fedcba9876543210fedcba9876543210fedcba98";

    public const string ApiDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    public const string NextApiDigest = "sha256:3333333333333333333333333333333333333333333333333333333333333333";

    /// <summary>Adds a product and returns its id.</summary>
    public static async Task<int> AddProductAsync(
        this AuthTestHost host, string name, ProductReleaseMode mode = ProductReleaseMode.Releases) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var product = TestProducts.New(name);
        product.ReleaseMode = mode;
        db.Products.Add(product);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return product.Id;
    }

    /// <summary>Adds a stack of an existing product — a tenant when given a template — and returns its id.</summary>
    public static async Task<int> AddProductStackAsync(
        this AuthTestHost host, string name, int productId,
        AutoDeployMode autoDeploy = AutoDeployMode.Off,
        StackDesiredState desiredState = StackDesiredState.Running,
        int? pinnedReleaseId = null,
        int? templateId = null,
        string? tenantSlug = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = name,
            ComposeProjectName = name,
            ProductId = productId,
            AutoDeployMode = autoDeploy,
            AutoDeployTime = autoDeploy == AutoDeployMode.Scheduled ? "02:00" : null,
            DesiredState = desiredState,
            PinnedReleaseId = pinnedReleaseId,
            TemplateId = templateId,
            TenantSlug = tenantSlug ?? (templateId is null ? null : name),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return stack.Id;
    }

    /// <summary>Adds a template over an existing product and returns its id.</summary>
    public static async Task<int> AddProductTemplateAsync(
        this AuthTestHost host, string name, int productId, int? defaultPinnedReleaseId = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var template = new StackTemplate {
            Name = name,
            ProductId = productId,
            DefaultPinnedReleaseId = defaultPinnedReleaseId,
            DomainPattern = "{tenant}.example.com",
            TargetServiceName = "web",
            TargetPort = 8080,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.StackTemplates.Add(template);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return template.Id;
    }

    /// <summary>The template's fleet default, as the database has it.</summary>
    public static async Task<int?> TemplateDefaultAsync(this AuthTestHost host, int templateId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.StackTemplates.AsNoTracking()
            .Where(t => t.Id == templateId)
            .Select(t => t.DefaultPinnedReleaseId)
            .FirstAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Adds a deploy event of a release — the rows the rollout view groups.</summary>
    public static async Task<int> AddReleaseDeployEventAsync(
        this AuthTestHost host, int stackId, int? releaseId, string status,
        string trigger = "release") {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var deployEvent = new DeployEvent {
            StackId = stackId,
            ReleaseId = releaseId,
            TriggeredBy = trigger,
            Status = status,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = status is "success" or "failed" ? DateTimeOffset.UtcNow : null,
        };
        db.DeployEvents.Add(deployEvent);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return deployEvent.Id;
    }

    /// <summary>Sets a product's release-retention floor — what the pruning pass reads.</summary>
    public static async Task SetRetainReleasesAsync(this AuthTestHost host, int productId, int retain) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Products.Where(p => p.Id == productId)
            .ExecuteUpdateAsync(
                p => p.SetProperty(x => x.RetainReleases, retain), TestContext.Current.CancellationToken);
    }

    /// <summary>The product's surviving release ids, oldest first.</summary>
    public static async Task<List<int>> ReleaseIdsAsync(this AuthTestHost host, int productId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Releases.AsNoTracking()
            .Where(r => r.ProductId == productId)
            .OrderBy(r => r.Id)
            .Select(r => r.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Adds a release with its images and returns its id. Newest is the highest id.</summary>
    public static async Task<int> AddReleaseAsync(
        this AuthTestHost host, int productId, string version, string? commitSha = ReleaseCommit,
        params (string Repository, string Digest)[] images) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var release = new Release {
            ProductId = productId,
            Version = version,
            CommitSha = commitSha,
            Branch = TestProducts.DefaultBranch,
            // Unique per product, and nothing here asserts about its construction — that is
            // ReleaseIntakeTests' subject.
            Fingerprint = $"fingerprint-{productId}-{version}",
            CreatedVia = Release.ViaWebhook,
            CreatedAt = DateTimeOffset.UtcNow,
            Images = [.. (images.Length > 0 ? images : [("ghcr.io/acme/api", ApiDigest)])
                .Select(i => new ReleaseImage { Repository = i.Repository, Digest = i.Digest })],
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return release.Id;
    }

    /// <summary>Pins a stack, or clears its pin — what <c>stacks.setRelease</c> writes.</summary>
    public static async Task PinAsync(this AuthTestHost host, int stackId, int? releaseId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Stacks.Where(s => s.Id == stackId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.PinnedReleaseId, releaseId),
                TestContext.Current.CancellationToken);
    }

    /// <summary>Marks a stack as already on a release — what a successful deploy records.</summary>
    public static async Task SetDeployedReleaseAsync(this AuthTestHost host, int stackId, int? releaseId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Stacks.Where(s => s.Id == stackId)
            .ExecuteUpdateAsync(
                s => s.SetProperty(x => x.LastDeployedReleaseId, releaseId),
                TestContext.Current.CancellationToken);
    }

    /// <summary>Flips a product's mode — what the first release does, and what an operator can undo.</summary>
    public static async Task SetReleaseModeAsync(
        this AuthTestHost host, int productId, ProductReleaseMode mode) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Products.Where(p => p.Id == productId)
            .ExecuteUpdateAsync(
                p => p.SetProperty(x => x.ReleaseMode, mode), TestContext.Current.CancellationToken);
    }

    /// <summary>The stack's pin and deployed release, as the database has them.</summary>
    public static async Task<(int? Pinned, int? LastDeployed)> ReleaseStateAsync(
        this AuthTestHost host, int stackId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == stackId)
            .Select(s => new { s.PinnedReleaseId, s.LastDeployedReleaseId })
            .FirstAsync(TestContext.Current.CancellationToken);
        return (row.PinnedReleaseId, row.LastDeployedReleaseId);
    }
}
