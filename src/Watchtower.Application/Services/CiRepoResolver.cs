using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// What a product's CI link resolves to: whether its remote can have runners at all (github.com only),
/// the parsed <c>owner/name</c>, and the <see cref="CiRepo"/> when one is configured for that pair.
/// </summary>
public readonly record struct CiRepoLink(bool IsGitHub, string? Owner, string? Name, CiRepo? Repo) {
    /// <summary>The answer for every non-GitHub remote: nothing to parse, nothing to link.</summary>
    public static CiRepoLink NotGitHub => new(false, null, null, null);
}

/// <summary>
/// Resolves the <see cref="Product.CiRepoId"/> link on read paths, and records it the first time it can
/// be worked out (ADR-0026 decision 7). Before the FK existed the link was recomputed from
/// <see cref="Product.RepositoryUrl"/> on every single read; the FK stores the answer, and this is the
/// one place that fills it in for products created — or migrated — before CI was enabled.
/// </summary>
/// <remarks>
/// Linking is best-effort by design: it is a cache write on a read path, so a lost race or a failed
/// statement must never turn a CI query into an error. The write is conditional
/// (<c>WHERE ci_repo_id IS NULL</c>) so the loser of a race is a no-op rather than an overwrite.
/// </remarks>
public sealed class CiRepoResolver(WatchtowerDbContext db, ILogger<CiRepoResolver> logger) {
    /// <summary>
    /// The CI link of <paramref name="product"/>, persisting the FK when it had to be derived from the
    /// repository URL.
    /// </summary>
    /// <remarks>
    /// Pass a product loaded <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}"/>: the
    /// link write goes through <c>ExecuteUpdate</c>, which bumps the row's <c>xmin</c> without telling
    /// the change tracker, so persisting behind a tracked instance would make that instance's next
    /// <c>SaveChanges</c> fail on concurrency. A tracked product is therefore resolved but not linked —
    /// writers (<c>ci.enableForProduct</c>) set the FK themselves, and the next read path links it.
    /// </remarks>
    public async Task<CiRepoLink> ResolveAsync(Product product, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(product);
        if (GitHubRepoUrl.TryParse(product.RepositoryUrl) is not var (owner, name))
            return CiRepoLink.NotGitHub;

        // The recorded link wins: it is what ci.enableForProduct wrote, and it survives spelling
        // differences in the URL. A dangling id cannot normally exist (the FK is SET NULL on delete),
        // but re-resolving rather than reporting "no CI" is the harmless answer if one ever does.
        if (product.CiRepoId is { } linkedId
            && await db.CiRepos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == linkedId, ct) is { } linked) {
            return new CiRepoLink(true, owner, name, linked);
        }

        var match = await FindByOwnerNameAsync(owner, name, tracked: false, ct);
        if (match is not null)
            await TryLinkAsync(product, match.Id, ct);
        return new CiRepoLink(true, owner, name, match);
    }

    /// <summary>
    /// The tracked <see cref="CiRepo"/> a product would enable CI on: the linked one when the FK is set,
    /// else the one matching the parsed <c>owner/name</c>. Null when CI was never enabled for it.
    /// </summary>
    public async Task<CiRepo?> FindForWriteAsync(Product product, string owner, string name, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(product);
        if (product.CiRepoId is { } linkedId
            && await db.CiRepos.FirstOrDefaultAsync(r => r.Id == linkedId, ct) is { } linked) {
            return linked;
        }
        return await FindByOwnerNameAsync(owner, name, tracked: true, ct);
    }

    /// <summary>
    /// GitHub compares <c>owner/name</c> case-insensitively, so the lookup does too — matching the
    /// <c>ix_ci_repos_owner_name_lower</c> expression index.
    /// </summary>
    private Task<CiRepo?> FindByOwnerNameAsync(string owner, string name, bool tracked, CancellationToken ct) =>
        (tracked ? db.CiRepos : db.CiRepos.AsNoTracking()).FirstOrDefaultAsync(
            r => r.Owner.ToLower() == owner.ToLower() && r.Name.ToLower() == name.ToLower(), ct);

    private async Task TryLinkAsync(Product product, int ciRepoId, CancellationToken ct) {
        if (product.CiRepoId == ciRepoId || db.Entry(product).State is not EntityState.Detached)
            return;
        try {
            await db.Products
                .Where(p => p.Id == product.Id && p.CiRepoId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CiRepoId, ciRepoId), ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            // A cache write, not the answer — the caller already has its link.
            logger.LogDebug(ex, "Could not record the CI repo link for product {ProductId}", product.Id);
        }
    }
}
