using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// Rolls the product's latest release out to every stack that tracks latest — the operator's "deploy
/// this everywhere", and the way a fleet kept on <c>Off</c> is moved forward deliberately.
/// </summary>
/// <remarks>
/// <para>
/// Convergent like every other release deploy (invariant 3): each enqueued deploy resolves
/// <c>PinnedReleaseId ?? newest</c> when it runs, so a release published while a 200-tenant rollout is
/// still draining is picked up by the tenants that have not started yet instead of being overwritten by
/// them. <see cref="Command.ReleaseId"/> is therefore advisory — it exists so the caller can say which
/// release it believed it was rolling out, and be refused if that is no longer the newest.
/// </para>
/// <para>
/// The target predicate is <see cref="ReleaseRolloutService.EnqueueLatestForProductAsync"/>'s, which
/// deliberately ignores <see cref="AutoDeployMode"/>: an operator pressing a button is not the stack
/// deploying by itself, and a canary fleet parked on <c>Off</c> — the workflow design.md §Rollback and
/// canary is built on — must be reachable by it. Pinned stacks are still excluded (a pin is a standing
/// instruction, and <c>stacks.setRelease</c> is how it is lifted) and so are stopped ones.
/// </para>
/// </remarks>
[Handler("products.deployRelease")]
public sealed class DeployRelease(
    WatchtowerDbContext db, ReleaseRolloutService rollout, AuditLog audit, ICurrentUser currentUser)
    : IHandler<DeployRelease.Command, Result<DeployRelease.Response>> {
    /// <summary>Audit action recorded for an operator-triggered rollout.</summary>
    public const string AuditAction = "release.deploy";

    /// <param name="ReleaseId">
    /// Optional guard: when supplied and it is not the product's newest release, the call is refused
    /// rather than quietly rolling out something else. Omit to mean "whatever is newest now".
    /// </param>
    public sealed record Command(int ProductId, int? ReleaseId = null);

    /// <param name="StacksEnqueued">How many stacks were targeted; zero is a legitimate answer.</param>
    /// <param name="DeployEventIds">The tracking events, one per targeted stack (coalescing may repeat one).</param>
    public sealed record Response(
        int ReleaseId, string Version, int StacksEnqueued, IReadOnlyList<int> DeployEventIds);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var product = await db.Products.AsNoTracking()
            .Where(p => p.Id == command.ProductId)
            .Select(p => new { p.Id, p.Name, p.ReleaseMode })
            .FirstOrDefaultAsync(ct);
        if (product is null)
            return AppError.NotFound($"Product {command.ProductId} not found.");
        if (product.ReleaseMode != ProductReleaseMode.Releases) {
            return AppError.Conflict(
                $"Product '{product.Name}' is in Git mode, so its stacks deploy the branch head rather "
                + "than a release. Switch it to release mode first.");
        }

        // Newest is the highest id (invariant 7) — never a timestamp.
        var latest = await db.Releases.AsNoTracking()
            .Where(r => r.ProductId == product.Id)
            .OrderByDescending(r => r.Id)
            .Select(r => new { r.Id, r.Version })
            .FirstOrDefaultAsync(ct);
        if (latest is null)
            return AppError.BusinessRule($"Product '{product.Name}' has no releases to deploy.");
        if (command.ReleaseId is { } requested && requested != latest.Id) {
            return AppError.Conflict(
                $"Release {requested} is no longer the newest release of '{product.Name}' — "
                + $"'{latest.Version}' is. Roll that out, or pin the stacks you meant.");
        }

        var result = await rollout.EnqueueLatestForProductAsync(product.Id, ct);

        await audit.RecordAsync(
            ProductMapping.AuditCategory, AuditAction, $"{product.Name}/{latest.Version}",
            $"{result.StacksEnqueued} stack(s) enqueued (latest-tracking, running)",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(latest.Id, latest.Version, result.StacksEnqueued, result.DeployEventIds);
    }
}
