using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// The releases of one product, newest first — the Releases tab's list.
/// </summary>
/// <remarks>
/// Keyset paging on the id rather than an offset: the id <em>is</em> the ordering key (ADR-0026), so
/// "the page after release 41" is a range scan that cannot skip or repeat a row when a release is
/// published while somebody is paging. <c>Before</c> is exclusive and comes from the last row of the
/// previous page.
/// </remarks>
[Handler("products.listReleases")]
public sealed class ListReleases(WatchtowerDbContext db)
    : IHandler<ListReleases.Query, Result<ListReleases.Response>> {
    /// <summary>Rows per page when the caller names no limit — the design's "20 + Show older".</summary>
    public const int DefaultLimit = 20;

    /// <summary>Hard ceiling on one page, so a client cannot ask for the whole history at once.</summary>
    public const int MaxLimit = 100;

    /// <param name="Before">Return releases with an id lower than this; null starts at the newest.</param>
    /// <param name="Limit">How many rows to return, clamped to 1…<see cref="MaxLimit"/>.</param>
    public sealed record Query(int ProductId, int? Before = null, int Limit = DefaultLimit) : IQuery;

    /// <param name="HasMore">Whether another page exists — what the "Show older" button keys on.</param>
    public sealed record Response(IReadOnlyList<ReleaseDto> Releases, bool HasMore);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        if (!await db.Products.AsNoTracking().AnyAsync(p => p.Id == query.ProductId, ct))
            return AppError.NotFound($"Product {query.ProductId} not found.");

        var limit = Math.Clamp(query.Limit, 1, MaxLimit);

        // One row more than asked for: whether a further page exists is the only thing the caller needs
        // beyond the page itself, and a count query over the whole history to answer it would cost more.
        var rows = await db.Releases.AsNoTracking()
            .Where(r => r.ProductId == query.ProductId)
            .Where(r => query.Before == null || r.Id < query.Before)
            .OrderByDescending(r => r.Id)
            .Take(limit + 1)
            .Select(r => new ReleaseDto(
                r.Id, r.Version, r.CommitSha, r.Branch, r.CreatedVia, r.CreatedAt, r.PublishedAt,
                r.SourceRunUrl, r.Images.Count))
            .ToListAsync(ct);

        var hasMore = rows.Count > limit;
        if (hasMore) rows.RemoveAt(rows.Count - 1);
        return new Response(rows, hasMore);
    }
}
