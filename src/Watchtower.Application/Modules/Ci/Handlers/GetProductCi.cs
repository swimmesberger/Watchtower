using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>
/// The CI view of one product: whether its repository URL is a GitHub repo (only those can get
/// runners), and the linked <see cref="CiRepoDto"/> — runner status and toolchain profile included —
/// when CI is enabled for it. This is the CI tab of the product page.
/// </summary>
/// <remarks>
/// The link is the <see cref="Entities.Product.CiRepoId"/> FK (ADR-0026 decision 7); products whose FK
/// is still null — everything created before this stage, and everything the backfill migration made —
/// are resolved from the repository URL once and then recorded, so the parse happens at most once per
/// product rather than on every read.
/// </remarks>
[Handler("ci.getProductCi")]
public sealed class GetProductCi(
    WatchtowerDbContext db, CiRepoResolver resolver, CiRunnerOrchestrator orchestrator)
    : IHandler<GetProductCi.Query, Result<GetProductCi.Response>> {
    public sealed record Query(int ProductId) : IQuery;

    public sealed record Response(CiLinkDto Ci);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var product = await db.Products.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == query.ProductId, ct);
        if (product is null)
            return AppError.NotFound($"Product {query.ProductId} not found.");

        var link = await resolver.ResolveAsync(product, ct);
        if (!link.IsGitHub)
            return new Response(new CiLinkDto(IsGitHub: false, Owner: null, Name: null, Repo: null));

        var dto = link.Repo is null
            ? null
            : CiMapping.ToDto(link.Repo, orchestrator.Status.TryGetValue(link.Repo.Id, out var s) ? s : null);
        return new Response(new CiLinkDto(IsGitHub: true, link.Owner, link.Name, dto));
    }
}
