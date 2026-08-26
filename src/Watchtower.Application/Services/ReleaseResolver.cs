using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// The one place that answers "which release would this stack deploy right now?" — the counterpart of
/// <see cref="ProductSourceResolver"/> for ADR-0026's release half.
/// </summary>
/// <remarks>
/// <para>
/// The rule is <c>PinnedReleaseId ?? newest release of the product</c>, and it is a rule about
/// <em>now</em>: it is evaluated at deploy execution time, never captured at enqueue (invariant 3,
/// design.md §Convergent fan-out). Capturing the id when the deploy is queued is what reintroduces the
/// downgrade race a slow fan-out makes routine.
/// </para>
/// <para>
/// Static, like <see cref="ProductSourceResolver"/>, but it takes a <see cref="WatchtowerDbContext"/>
/// because "newest" is a query rather than a property of rows the caller already has. A <c>Git</c>-mode
/// product resolves to null without the query running at all — the mode check comes first, so the Git
/// deploy path provably never reaches the releases table.
/// </para>
/// </remarks>
public static class ReleaseResolver {
    /// <summary>
    /// The gate for the whole feature: whether <paramref name="product"/> deploys releases at all.
    /// </summary>
    /// <remarks>
    /// Written once and asked twice — here inside <see cref="ResolveAsync"/>, which is the contract, and
    /// at the deploy's call site, which uses it to avoid opening a database scope for an answer it knows
    /// will be null. One predicate rather than two copies of it, so "a <c>Git</c>-mode product resolves
    /// no release" has a single place it can be wrong, and a single place a test can prove it right.
    /// </remarks>
    public static bool UsesReleases(Product product) {
        ArgumentNullException.ThrowIfNull(product);
        return product.ReleaseMode == ProductReleaseMode.Releases;
    }

    /// <summary>
    /// The release <paramref name="stack"/> would deploy, or null when its product is in <c>Git</c>
    /// mode or has no releases at all. <paramref name="stack"/> must have its product loaded.
    /// </summary>
    /// <param name="stack">The stack, with <see cref="Stack.Product"/> included.</param>
    /// <param name="db">The context to read the product's releases through.</param>
    /// <param name="logger">
    /// Optional, and used for exactly one line: a pin that names nothing this stack could deploy. The
    /// resolver is static and otherwise silent, so the caller lends its own logger rather than this
    /// becoming a service to carry one.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static Task<Release?> ResolveAsync(
        Stack stack, WatchtowerDbContext db, ILogger? logger, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(stack);
        ArgumentNullException.ThrowIfNull(db);
        var product = RequireProduct(stack);

        // In Git mode there is no release to resolve, so nothing below this line runs and the deploy
        // behaves exactly as it did before ADR-0026.
        if (!UsesReleases(product)) return Task.FromResult<Release?>(null);

        return stack.PinnedReleaseId is { } pinnedId
            ? FindPinnedAsync(db, stack, pinnedId, logger, ct)
            : NewestAsync(db, product.Id, ct);
    }

    /// <summary>The stack's product, or the include-you-forgot exception.</summary>
    public static Product RequireProduct(Stack stack) {
        ArgumentNullException.ThrowIfNull(stack);
        return stack.Product ?? throw new InvalidOperationException(
            $"Stack {stack.Id} was loaded without its product. Include(s => s.Product) — the release "
            + "mode lives there since ADR-0026.");
    }

    /// <summary>
    /// The newest release of a product: the one with the highest <see cref="Release.Id"/>, images
    /// included. Null when the product has none.
    /// </summary>
    /// <remarks>
    /// Ordered by id and by nothing else (invariant 7): <see cref="Release.CreatedAt"/> and
    /// <see cref="Release.PublishedAt"/> are display values, and two instances writing releases a second
    /// apart must not be able to invert the order by disagreeing about the time.
    /// </remarks>
    public static Task<Release?> NewestAsync(WatchtowerDbContext db, int productId, CancellationToken ct) =>
        db.Releases.AsNoTracking()
            .Include(r => r.Images)
            .Where(r => r.ProductId == productId)
            .OrderByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The pinned release, images included — filtered to the stack's own product. Null, with a warning,
    /// when the pin names nothing this stack could deploy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The product filter is not defensive noise: a release of a <em>different</em> product pins digests
    /// this stack's compose file can never match, so honouring such a pin would deploy an unpinned stack
    /// that every surface calls pinned. <c>stacks.setRelease</c> refuses to write one and the
    /// <c>Restrict</c> foreign key stops the pinned release being deleted, so the only way here is a
    /// hand-edited database — which is exactly the case worth catching rather than acting on.
    /// </para>
    /// <para>
    /// Null means the deploy falls through to the branch head, which is the safe direction but a
    /// surprising one for whoever set the pin, so it is logged rather than silent. Deliberately not a
    /// failed deploy: refusing would take a stack down over a stale row, and the deploy output already
    /// says which release (if any) it is applying.
    /// </para>
    /// </remarks>
    private static async Task<Release?> FindPinnedAsync(
        WatchtowerDbContext db, Stack stack, int pinnedReleaseId, ILogger? logger, CancellationToken ct) {
        var release = await db.Releases.AsNoTracking()
            .Include(r => r.Images)
            .FirstOrDefaultAsync(r => r.Id == pinnedReleaseId && r.ProductId == stack.ProductId, ct);
        if (release is null) {
            logger?.LogWarning(
                "Stack {StackId} is pinned to release {ReleaseId}, which is not a release of its "
                + "product {ProductId} (deleted, or repointed by hand). Falling back to the branch head.",
                stack.Id, pinnedReleaseId, stack.ProductId);
        }
        return release;
    }
}
