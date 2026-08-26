using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>What one pruning pass removed.</summary>
/// <param name="Deleted">How many releases were deleted; zero is the normal answer.</param>
/// <param name="DeletedIds">Their ids, oldest first — what the audit row names.</param>
/// <param name="Protected">
/// How many releases beyond the retention floor were kept anyway because a protection rule named them.
/// Recorded so "why does this product still have 300 releases?" is answerable from the trail.
/// </param>
public sealed record ReleasePruneResult(int Deleted, IReadOnlyList<int> DeletedIds, int Protected) {
    /// <summary>Nothing was over the floor, or everything over it was protected.</summary>
    public static readonly ReleasePruneResult None = new(0, [], 0);
}

/// <summary>
/// Release retention (docs/products/design.md §"Release retention", risk 11): keeps the newest
/// <see cref="Product.RetainReleases"/> releases of a product and deletes the rest — except the ones
/// something still depends on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The four protection rules are the whole point of this class</b>, and each of them protects
/// against a different way a delete would reach out and change something that is not retention's
/// business:
/// </para>
/// <list type="number">
/// <item><b>Pinned by a stack.</b> The <c>Restrict</c> foreign key would throw rather than allow it,
/// which would make one hand-pinned tenant break every future pruning pass of its product. Excluding
/// it in the query is what keeps housekeeping quiet.</item>
/// <item><b>Named as a template's <see cref="StackTemplate.DefaultPinnedReleaseId"/>.</b> That FK is
/// <c>SET NULL</c>, so there is nothing to throw: the delete would succeed and the next tenant
/// provisioned from that template would silently track latest instead of the fleet default somebody
/// chose. The nightmare case, and the reason this rule is separate from the first.</item>
/// <item><b>Recorded as a stack's <see cref="Stack.LastDeployedReleaseId"/>.</b> Also <c>SET NULL</c>:
/// the delete would blank out "what is this stack actually running", which is the question every
/// version surface answers.</item>
/// <item><b>Referenced by any stored <see cref="DeployEvent"/>.</b> Also <c>SET NULL</c>, and the same
/// loss: the rollout view groups events by release, and pruning would empty it. <em>Any</em> event
/// rather than a recent one, because <c>deploy_events</c> has no retention of its own (design.md notes
/// the gap) — so "recent" has no definition here that would not be invented, and the honest rule is
/// that history keeps what history references.</item>
/// </list>
/// <para>
/// <b>Where it runs.</b> Post-create inside <see cref="ReleaseIntakeService"/>: cheap, event-driven,
/// and it needs no background loop of its own — the only way a product grows past its floor is by
/// gaining a release. A pruning failure is caught there and never fails the intake it rode in on.
/// </para>
/// </remarks>
/// <param name="db">Scoped Watchtower database context.</param>
/// <param name="audit">Audit trail; one row per pass that actually deleted something.</param>
public class ReleasePruner(WatchtowerDbContext db, AuditLog audit) {
    /// <summary>Audit action recorded for a pass that deleted at least one release.</summary>
    public const string AuditAction = "release.prune";

    /// <summary>The retention floor a product is created with.</summary>
    public const int DefaultRetainReleases = 50;

    /// <summary>
    /// Fewest releases retention will ever keep. A floor under the floor: pruning down to one release
    /// would leave a product with nothing to roll back to, which is the opposite of what releases are
    /// for.
    /// </summary>
    public const int MinRetainReleases = 5;

    /// <summary>Most releases retention will keep — past this the pass is effectively off.</summary>
    public const int MaxRetainReleases = 1000;

    /// <summary>How many release ids one audit detail names before it switches to a count.</summary>
    private const int NamedIds = 20;

    /// <summary><paramref name="retain"/> brought into <see cref="MinRetainReleases"/>…<see cref="MaxRetainReleases"/>.</summary>
    public static int Clamp(int retain) => Math.Clamp(retain, MinRetainReleases, MaxRetainReleases);

    /// <summary>
    /// Deletes the releases of <paramref name="productId"/> that are older than its retention floor and
    /// that nothing depends on.
    /// </summary>
    /// <remarks>
    /// Ordered by id throughout (invariant 7): "the newest N" is the N highest ids, never the N latest
    /// timestamps. The protection sets are read as ids rather than joined per row so the delete is one
    /// statement over a known list, and so the audit detail can name exactly what went.
    /// </remarks>
    /// <param name="productId">The product to prune.</param>
    /// <param name="actor">Audit actor; null for the webhook path, where nobody is signed in.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ReleasePruneResult> PruneAsync(int productId, string? actor, CancellationToken ct) {
        var product = await db.Products.AsNoTracking()
            .Where(p => p.Id == productId)
            .Select(p => new { p.Name, p.RetainReleases })
            .FirstOrDefaultAsync(ct);
        if (product is null) return ReleasePruneResult.None;

        var retain = Clamp(product.RetainReleases);

        // Everything below the retention window, oldest first. Skip() over an id-ordered query is the
        // "newest N" rule expressed once; the window itself is never materialized.
        var candidates = await db.Releases.AsNoTracking()
            .Where(r => r.ProductId == productId)
            .OrderByDescending(r => r.Id)
            .Skip(retain)
            .Select(r => r.Id)
            .ToListAsync(ct);
        if (candidates.Count == 0) return ReleasePruneResult.None;

        var keep = await ProtectedIdsAsync(candidates, ct);
        var doomed = candidates.Where(id => !keep.Contains(id)).OrderBy(id => id).ToList();
        if (doomed.Count == 0) return new ReleasePruneResult(0, [], candidates.Count);

        // The images cascade with the rows. ExecuteDelete rather than loading entities: nothing here
        // needs the release objects, and a fleet that has accumulated thousands should not page them
        // through the change tracker to throw them away.
        await db.Releases.Where(r => doomed.Contains(r.Id)).ExecuteDeleteAsync(ct);

        await audit.RecordAsync(
            ProductCatalog.AuditCategory, AuditAction, product.Name,
            $"{doomed.Count} release(s) pruned beyond the newest {retain} ({DescribeIds(doomed)})"
            + (candidates.Count - doomed.Count is var kept && kept > 0
                ? $"; {kept} kept (pinned, a template default, deployed, or named by deploy history)"
                : string.Empty),
            actor: actor, ct: ct);

        return new ReleasePruneResult(doomed.Count, doomed, candidates.Count - doomed.Count);
    }

    /// <summary>
    /// The subset of <paramref name="candidates"/> a protection rule names — see the four rules in the
    /// remarks on this class.
    /// </summary>
    private async Task<HashSet<int>> ProtectedIdsAsync(
        IReadOnlyList<int> candidates, CancellationToken ct) {
        var pinned = await db.Stacks.AsNoTracking()
            .Where(s => s.PinnedReleaseId != null && candidates.Contains(s.PinnedReleaseId.Value))
            .Select(s => s.PinnedReleaseId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var templateDefaults = await db.StackTemplates.AsNoTracking()
            .Where(t => t.DefaultPinnedReleaseId != null
                && candidates.Contains(t.DefaultPinnedReleaseId.Value))
            .Select(t => t.DefaultPinnedReleaseId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var deployed = await db.Stacks.AsNoTracking()
            .Where(s => s.LastDeployedReleaseId != null
                && candidates.Contains(s.LastDeployedReleaseId.Value))
            .Select(s => s.LastDeployedReleaseId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var referenced = await db.DeployEvents.AsNoTracking()
            .Where(e => e.ReleaseId != null && candidates.Contains(e.ReleaseId.Value))
            .Select(e => e.ReleaseId!.Value)
            .Distinct()
            .ToListAsync(ct);

        return [.. pinned, .. templateDefaults, .. deployed, .. referenced];
    }

    /// <summary>The deleted ids, or a count once naming them would be a wall of numbers.</summary>
    private static string DescribeIds(IReadOnlyList<int> ids) =>
        ids.Count <= NamedIds
            ? string.Join(", ", ids.Select(id => $"#{id}"))
            : $"#{ids[0]}…#{ids[^1]}";
}
