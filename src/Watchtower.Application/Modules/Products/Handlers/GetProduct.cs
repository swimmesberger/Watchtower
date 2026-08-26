using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// A product with the stacks and templates that reference it — the roster an operator needs before
/// editing a shared source, and the same list <c>products.delete</c> refuses with.
/// </summary>
[Handler("products.get")]
public sealed class GetProduct(WatchtowerDbContext db)
    : IHandler<GetProduct.Query, Result<GetProduct.Response>> {
    public sealed record Query(int Id) : IQuery;

    /// <param name="ReleaseWebhookToken">
    /// The product's release webhook bearer, or null when none has been generated. On the detail
    /// response rather than on <see cref="ProductDto"/> deliberately: the Releases tab has to be able
    /// to show it for copying (an operator pastes it into the repository's Actions secrets by hand
    /// until secret sync lands), while the catalogue — which lists every product — must not carry
    /// every product's secret.
    /// </param>
    /// <param name="UnreleasedCommitSha">
    /// The product's branch head, when it is a commit no release was built from — design.md's
    /// "latest ≠ branch head" warning ("2 commits on main since v1"), which the first-release transition
    /// makes routine and a re-run of an old workflow makes possible at any time.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>No network call.</b> The value is already in the database: <c>StackUpdateCheck.NewCommitSha</c>
    /// is the remote head the periodic check last saw for a stack, and in <c>Releases</c> mode it is kept
    /// deliberately informational for exactly this. Asking git for the head on a read path — a page load,
    /// once per product — is the thing this must not become.
    /// </para>
    /// <para>
    /// Only stacks that track the product's own branch are consulted (no <c>BranchOverride</c> of their
    /// own and none inherited from a template): a staging stack on <c>develop</c> polls a different head,
    /// and its commit says nothing about whether <c>main</c> has moved past the latest release. And a
    /// head that <em>equals</em> the latest release's commit is not a warning — that is precisely the
    /// case where the release is the branch head, reported only because the stack has not deployed it
    /// yet.
    /// </para>
    /// <para>
    /// It is therefore a lower bound on the truth, and honestly so: a product whose stacks are all on
    /// overrides, or which has never been polled, reports null rather than guessing. The UI names the
    /// commit and does not invent a count of how many there are — nothing here can know that without a
    /// clone.
    /// </para>
    /// </remarks>
    public sealed record Response(
        ProductDto Product,
        IReadOnlyList<ProductStackDto> Stacks,
        IReadOnlyList<ProductTemplateDto> Templates,
        string? ReleaseWebhookToken,
        string? UnreleasedCommitSha);

    /// <summary>
    /// One roster row as the query reads it — a named shape rather than an anonymous one so
    /// <see cref="UnreleasedCommit"/> can be a plain static over it.
    /// </summary>
    /// <param name="TracksProductBranch">
    /// Whether this stack deploys the product's own branch, with no override of its own and none
    /// inherited from a template. Only those stacks' polled heads say anything about the product's
    /// branch — see the remarks on <see cref="Response"/>.
    /// </param>
    /// <param name="NewCommitSha">The remote head the periodic check last saw, when it saw a new one.</param>
    private sealed record StackRow(
        int Id,
        string Name,
        string Branch,
        string? BranchOverride,
        int? TemplateId,
        string? TenantSlug,
        Entities.DeployStatus? LastDeployStatus,
        DateTimeOffset? LastDeployedAt,
        int? PinnedReleaseId,
        string? PinnedVersion,
        int? LastDeployedReleaseId,
        string? LastDeployedVersion,
        bool TracksProductBranch,
        string? NewCommitSha);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var product = await db.Products.AsNoTracking()
            .Include(p => p.Credential)
            .FirstOrDefaultAsync(p => p.Id == query.Id, ct);
        if (product is null)
            return AppError.NotFound($"Product {query.Id} not found.");

        // Projected rather than materialized as entities: the effective branch is the only resolved
        // value either roster needs, and it is a two-level coalesce the database can do.
        var rows = await db.Stacks.AsNoTracking()
            .Where(s => s.ProductId == product.Id)
            .OrderBy(s => s.Name)
            .Select(s => new StackRow(
                s.Id,
                s.Name,
                // The two-level coalesce ProductSourceResolver applies, done in SQL.
                s.BranchOverride
                    ?? (s.Template != null ? s.Template.BranchOverride : null)
                    ?? product.DefaultBranch,
                s.BranchOverride,
                s.TemplateId,
                s.TenantSlug,
                s.LastDeployStatus,
                s.LastDeployedAt,
                s.PinnedReleaseId,
                s.PinnedRelease != null ? s.PinnedRelease.Version : null,
                s.LastDeployedReleaseId,
                s.LastDeployedRelease != null ? s.LastDeployedRelease.Version : null,
                s.BranchOverride == null && (s.Template == null || s.Template.BranchOverride == null),
                s.UpdateCheck != null ? s.UpdateCheck.NewCommitSha : null))
            .ToListAsync(ct);
        // The wire form of the status is lowercase; the enum-to-string conversion is a client-side
        // detail rather than something to ask the database to reproduce.
        var stacks = rows
            .Select(r => new ProductStackDto(
                r.Id, r.Name, r.Branch, r.BranchOverride, r.TemplateId, r.TenantSlug,
                r.LastDeployStatus?.ToString().ToLowerInvariant(), r.LastDeployedAt,
                r.PinnedReleaseId is null ? ProductMapping.TrackingLatest : ProductMapping.TrackingPinned,
                ProductMapping.ReleaseRef(r.PinnedReleaseId, r.PinnedVersion),
                ProductMapping.ReleaseRef(r.LastDeployedReleaseId, r.LastDeployedVersion)))
            .ToList();

        var templates = await db.StackTemplates.AsNoTracking()
            .Where(t => t.ProductId == product.Id)
            .OrderBy(t => t.Name)
            .Select(t => new ProductTemplateDto(
                t.Id, t.Name, t.BranchOverride ?? product.DefaultBranch, t.BranchOverride, t.Instances.Count))
            .ToListAsync(ct);

        // Newest is the highest id (ADR-0026) — the ordering never comes from a timestamp.
        var latest = await db.Releases.AsNoTracking()
            .Where(r => r.ProductId == product.Id)
            .OrderByDescending(r => r.Id)
            .Select(r => new ProductReleaseSummaryDto(r.Id, r.Version, r.CreatedAt, r.CommitSha))
            .FirstOrDefaultAsync(ct);

        var dto = ProductMapping.ToDto(
            product, product.Credential?.Name, stacks.Count, templates.Count, latest);
        return new Response(
            dto, stacks, templates, product.ReleaseWebhookToken, UnreleasedCommit(rows, latest));
    }

    /// <summary>
    /// The branch head no release was built from, or null. See the remarks on
    /// <see cref="Response.UnreleasedCommitSha"/> for what this can and cannot know.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Case-insensitive because the two sides come from different places: intake lower-cases the commit
    /// it stores, and <c>git ls-remote</c>'s answer is passed through as it was printed.
    /// </para>
    /// <para>
    /// <b>The tie-break, when several stacks disagree about the head, is the first by stack name</b> —
    /// the order the roster query already imposes, so the page and this line name the same commit and
    /// the answer is stable between reads rather than dependent on which row the database returned
    /// first. They can legitimately disagree: each stack's check ran at a different time, so two of them
    /// straddling a push see two heads. Any of those is a true "there are unreleased commits", which is
    /// all this claims; picking the *newest* would need ancestry, which is a clone.
    /// </para>
    /// <para>
    /// <b>A release with no commit suppresses the warning entirely</b> (<c>latest.CommitSha</c> is null,
    /// so nothing equals it and the first polled head wins). That is deliberate: such a release records
    /// only images and its deploy falls back to the branch head anyway, so "the branch has moved past
    /// the release" is exactly what is happening and worth saying. It is also unreachable today —
    /// intake requires a version when there is no commit, and every current source sends one.
    /// </para>
    /// </remarks>
    private static string? UnreleasedCommit(
        IEnumerable<StackRow> rows, ProductReleaseSummaryDto? latest) =>
        latest is null
            ? null
            : rows
                .Where(r => r.TracksProductBranch && !string.IsNullOrEmpty(r.NewCommitSha))
                .Select(r => r.NewCommitSha!)
                .FirstOrDefault(head =>
                    !string.Equals(head, latest.CommitSha, StringComparison.OrdinalIgnoreCase));
}
