using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Refreshes a <see cref="Entities.CiRepo"/>'s toolchain profile from a stack deploy's working
/// tree (docs/ci-runners/design.md). Deploys clone the repository anyway, so detection piggybacks
/// on that clone at zero extra cost. Strictly best-effort: any failure is logged and swallowed —
/// toolchain detection must never fail a deploy.
/// </summary>
public sealed class CiToolchainRecorder(
    IServiceScopeFactory scopeFactory,
    CiRunnerOrchestrator orchestrator,
    ILogger<CiToolchainRecorder> logger) {

    /// <summary>
    /// Detects the toolchain profile of <paramref name="cloneDir"/> and persists it on the CI repo
    /// linked to product <paramref name="productId"/>, when one is configured. No-op for non-GitHub
    /// remotes and for products without CI enabled. Returns a short human-readable summary for
    /// the deploy log, or null when nothing was recorded.
    /// </summary>
    /// <remarks>
    /// Keyed by product, not by URL: the link is <see cref="Entities.Product.CiRepoId"/> since ADR-0026,
    /// and the resolver falls back to parsing the repository URL (recording the FK when it finds a
    /// match) for products that predate it.
    /// </remarks>
    public async Task<string?> TryRecordAsync(int productId, string cloneDir, CancellationToken ct) {
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, ct);
            if (product is null)
                return null;

            var link = await scope.ServiceProvider.GetRequiredService<CiRepoResolver>()
                .ResolveAsync(product, ct);
            // Attach the resolved (no-tracking) repo so the profile write below goes through EF as an
            // update rather than an insert.
            if (link.Repo is not { } repo)
                return null;
            db.CiRepos.Attach(repo);

            var profile = CiToolchainDetector.Detect(cloneDir);
            var json = profile.ToJson();
            var changed = repo.ToolchainProfileJson != json;
            repo.ToolchainProfileJson = json;
            repo.ToolchainDetectedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            if (changed) {
                // The orchestrator compares profile hash vs. warmed hash and re-warms on drift.
                orchestrator.RequestReconcile();
            }

            var summary = profile.IsEmpty
                ? "no known toolchains"
                : string.Join(", ", profile.Toolchains.Select(t => $"{t.Kind} {t.Version}")
                    .Concat(profile.HasDockerfile ? ["Dockerfile"] : Array.Empty<string>()));
            return $"CI toolchain profile for {repo.FullName}: {summary}"
                   + (changed ? " (changed — toolcache warm-up scheduled)" : "");
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogWarning(ex, "CI toolchain detection failed for product {ProductId}; deploy continues", productId);
            return null;
        }
    }
}
