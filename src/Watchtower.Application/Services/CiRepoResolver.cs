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
/// <para>
/// Linking is best-effort by design: it is a cache write on a read path, so a lost race or a failed
/// statement must never turn a CI query into an error. The write is conditional
/// (<c>WHERE ci_repo_id IS NULL</c>) so the loser of a race is a no-op rather than an overwrite.
/// </para>
/// <para>
/// Because of that guard the link is written once and never corrected: a <c>products.update</c> that
/// moves the repository URL clears the FK, but a read racing that update can re-record the <em>old</em>
/// repo just after it. Correction therefore lives on the read path, not in the writer — every lookup
/// re-checks that the linked repo's <c>owner/name</c> still matches the product's parsed URL and falls
/// through to the <c>owner/name</c> lookup when it does not. Leaving <see cref="TryLinkAsync"/> a
/// write-if-null keeps it one statement that cannot overwrite a deliberate link — the FK is what
/// <c>ci.enableForProduct</c> wrote — and the stale row it may leave behind is then a row nothing
/// believes: reads ignore it, <c>ci.enableForProduct</c> writes the right id over it, and the next
/// <c>products.update</c> of the URL clears it.
/// </para>
/// </remarks>
public sealed class CiRepoResolver(WatchtowerDbContext db, ILogger<CiRepoResolver> logger) {
    /// <summary>
    /// The CI link of <paramref name="product"/>, persisting the FK when it had to be derived from the
    /// repository URL.
    /// </summary>
    /// <remarks>
    /// Pass a product loaded <see cref="EntityFrameworkQueryableExtensions.AsNoTracking{TEntity}"/>, and
    /// make sure <em>no</em> instance of that row is tracked by this <see cref="WatchtowerDbContext"/> —
    /// the constraint is on the row in this context's identity map, not merely on the object handed in.
    /// The link write goes through <c>ExecuteUpdate</c>, which bumps the row's <c>xmin</c> without
    /// telling the change tracker, so any tracked instance of the same row would fail its next
    /// <c>SaveChanges</c> on a phantom concurrency conflict. This is the one xmin hazard that
    /// <see cref="Entities.IHasXmin"/> does <em>not</em> remove — making the token a real property fixed
    /// detach-and-attach, but nothing can make an in-memory copy notice a write that bypassed the
    /// tracker. A product handed in tracked is therefore
    /// resolved but not linked — writers (<c>ci.enableForProduct</c>) set the FK themselves, and the next
    /// read path links it. The guard in <see cref="TryLinkAsync"/> only sees the instance it is given, so
    /// a caller that loads the row twice (once tracked, once not) has to keep them apart itself.
    /// </remarks>
    public async Task<CiRepoLink> ResolveAsync(Product product, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(product);
        if (GitHubRepoUrl.TryParse(product.RepositoryUrl) is not var (owner, name))
            return CiRepoLink.NotGitHub;

        // The recorded link wins: it is what ci.enableForProduct wrote, and it survives spelling
        // differences in the URL — but only while it still describes the same repository. A link that no
        // longer matches the parsed owner/name is stale (a URL change whose FK clear lost a race with a
        // read's best-effort link write) and is ignored rather than trusted forever. A dangling id cannot
        // normally exist (the FK is SET NULL on delete), but re-resolving rather than reporting "no CI"
        // is the harmless answer if one ever does.
        if (product.CiRepoId is { } linkedId
            && await db.CiRepos.AsNoTracking().FirstOrDefaultAsync(r => r.Id == linkedId, ct) is { } linked
            && Matches(linked, owner, name)) {
            return new CiRepoLink(true, owner, name, linked);
        }

        var match = await FindByOwnerNameAsync(owner, name, tracked: false, ct);
        if (match is not null)
            await TryLinkAsync(product, match.Id, ct);
        return new CiRepoLink(true, owner, name, match);
    }

    /// <summary>
    /// The tracked <see cref="CiRepo"/> a product would enable CI on: the linked one when the FK is set
    /// and still describes <paramref name="owner"/>/<paramref name="name"/>, else the one matching that
    /// pair. Null when CI was never enabled for it.
    /// </summary>
    /// <remarks>
    /// The same staleness check as <see cref="ResolveAsync"/>, for the same reason: a link left over from
    /// a previous repository URL must not decide which <see cref="CiRepo"/> a write lands on.
    /// </remarks>
    public async Task<CiRepo?> FindForWriteAsync(Product product, string owner, string name, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(product);
        if (product.CiRepoId is { } linkedId
            && await db.CiRepos.FirstOrDefaultAsync(r => r.Id == linkedId, ct) is { } linked
            && Matches(linked, owner, name)) {
            return linked;
        }
        return await FindByOwnerNameAsync(owner, name, tracked: true, ct);
    }

    /// <summary>
    /// The reverse lookup the Actions-secret sync needs: every product whose release configuration this
    /// repo would carry — <see cref="Product.SyncReleaseSecrets"/> is on, and either the
    /// <see cref="Product.RepositoryUrl"/> parses to the repo's <c>owner/name</c> or
    /// <see cref="Product.CiRepoId"/> names it. Ordered by id, and tracked because callers stamp the
    /// sync state onto what comes back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The candidate set is filtered in SQL on the sync flag — at most a handful of rows instance-wide,
    /// and normally one per repository — and the match is then done in memory, because
    /// <see cref="GitHubRepoUrl.TryParse"/> is not translatable. Matching on the parsed URL rather than
    /// only on the FK is deliberate: the FK is a lazily recorded cache (see the remarks on this class)
    /// and is set to null when the <see cref="CiRepo"/> is deleted, so a sync that fired only when it
    /// happened to be filled in would be a silent no-op wearing a "synced" badge. The FK is matched
    /// <em>as well</em> so a row is still found if its URL ever stops parsing.
    /// </para>
    /// <para>
    /// <b>A list, not a single product, and that is the point.</b> The filtered unique index on
    /// <c>(ci_repo_id) WHERE sync_release_secrets</c> constrains only rows whose FK is set —
    /// PostgreSQL treats NULLs as distinct — so a product left over from a deleted CI repo can sit
    /// beside a legitimately syncing one. Returning the first by id would then push one product's
    /// token into the repository the other was wired for. The caller sees both and refuses.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<Product>> FindSyncingProductsAsync(CiRepo repo, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(repo);
        var candidates = await db.Products
            .Where(p => p.SyncReleaseSecrets)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
        return [.. candidates.Where(p =>
            p.CiRepoId == repo.Id
            || (GitHubRepoUrl.TryParse(p.RepositoryUrl) is { } parsed
                && string.Equals(parsed.Owner, repo.Owner, StringComparison.OrdinalIgnoreCase)
                && string.Equals(parsed.Name, repo.Name, StringComparison.OrdinalIgnoreCase)))];
    }

    /// <summary>
    /// GitHub compares <c>owner/name</c> case-insensitively, so the lookup does too — matching the
    /// <c>ix_ci_repos_owner_name_lower</c> expression index.
    /// </summary>
    private Task<CiRepo?> FindByOwnerNameAsync(string owner, string name, bool tracked, CancellationToken ct) =>
        (tracked ? db.CiRepos : db.CiRepos.AsNoTracking()).FirstOrDefaultAsync(
            r => r.Owner.ToLower() == owner.ToLower() && r.Name.ToLower() == name.ToLower(), ct);

    /// <summary>Whether a linked repo still is the one the product's URL parses to, GitHub's own casing rules.</summary>
    private static bool Matches(CiRepo repo, string owner, string name) =>
        string.Equals(repo.Owner, owner, StringComparison.OrdinalIgnoreCase)
        && string.Equals(repo.Name, name, StringComparison.OrdinalIgnoreCase);

    /// <remarks>
    /// The <see cref="EntityState.Detached"/> guard is a check on the instance handed in, but the
    /// constraint it stands for is wider: no instance of <em>this row</em> may be tracked by this
    /// <see cref="WatchtowerDbContext"/> when the <c>ExecuteUpdate</c> below bumps its <c>xmin</c> behind
    /// the change tracker's back. It catches the case that actually occurs — a writer passing its own
    /// tracked entity — and cannot catch a caller that separately loaded the same row tracked; see the
    /// remarks on <see cref="ResolveAsync"/>.
    /// </remarks>
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
            // A cache write, not the answer — the caller already has its link, so this is swallowed.
            // Warning rather than Debug: since the products table grew the filtered unique index on
            // (ci_repo_id) WHERE sync_release_secrets, a *constraint* violation can land here, and that
            // one is not a lost race — it means two syncing products are converging on one repository.
            // The sync pass refuses that state loudly on its own, but a swallowed exception at Debug
            // would be the last place anyone looked, so it is logged where it will actually be seen.
            logger.LogWarning(ex, "Could not record the CI repo link for product {ProductId}", product.Id);
        }
    }
}
