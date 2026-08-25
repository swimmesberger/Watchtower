using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// Removes a release and the images it pins. Allowed unconditionally in this stage because nothing
/// references a release yet — it is a record, not a dependency.
/// </summary>
/// <remarks>
/// <b>Stage 4 must add the pinned guard.</b> Once <c>Stack.PinnedReleaseId</c> exists it is a
/// <c>Restrict</c> foreign key on purpose (ADR-0026 decision 4): deleting a pinned release must be
/// refused, naming the stacks that pin it, rather than silently flipping them back to latest-tracking —
/// a deploy-behaviour change caused by a delete somewhere else. Until then there is nothing to check.
/// </remarks>
[Handler("products.deleteRelease")]
public sealed class DeleteRelease(WatchtowerDbContext db, AuditLog audit, ICurrentUser currentUser)
    : IHandler<DeleteRelease.Command, Result<DeleteRelease.Response>> {
    /// <summary>Audit action recorded for a deleted release.</summary>
    public const string AuditAction = "release.delete";

    public sealed record Command(int ReleaseId);
    public sealed record Response(int Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var release = await db.Releases
            .Include(r => r.Product)
            .FirstOrDefaultAsync(r => r.Id == command.ReleaseId, ct);
        if (release is null)
            return AppError.NotFound($"Release {command.ReleaseId} not found.");

        var target = $"{release.Product.Name}/{release.Version}";
        var detail = $"commit {ReleaseFingerprint.DescribeCommit(release.CommitSha)}; "
            + $"created via {release.CreatedVia}";
        // The images cascade with the row.
        db.Releases.Remove(release);
        await db.SaveChangesAsync(ct);

        // Past the commit point, like products.delete: a delete that failed must not leave a trail
        // claiming it happened.
        await audit.RecordAsync(
            ProductMapping.AuditCategory, AuditAction, target, detail,
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(command.ReleaseId);
    }
}
