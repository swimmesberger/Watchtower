using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// One release with the images it pins — what the expanded row in the Releases tab renders, and the
/// only place the digests are fetched. The list deliberately carries a count instead, so opening a
/// product does not load every digest of every build.
/// </summary>
[Handler("products.getRelease")]
public sealed class GetRelease(WatchtowerDbContext db)
    : IHandler<GetRelease.Query, Result<GetRelease.Response>> {
    public sealed record Query(int ReleaseId) : IQuery;

    public sealed record Response(ReleaseDetailDto Release);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var release = await db.Releases.AsNoTracking()
            .Include(r => r.Images)
            .Include(r => r.Product)
            .FirstOrDefaultAsync(r => r.Id == query.ReleaseId, ct);
        return release is null
            ? AppError.NotFound($"Release {query.ReleaseId} not found.")
            : new Response(ProductMapping.ToDetailDto(release, release.Product.Name));
    }
}
