using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// Removes a release and the images it pins, unless a stack is pinned to it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The pinned guard</b> (ADR-0026 decision 4): deleting a release a stack pins would silently flip
/// that stack back to latest-tracking, changing what it deploys because of an action taken somewhere
/// else entirely. It is refused, naming the stacks, so the operator unpins deliberately or picks a
/// different release. The <c>Restrict</c> foreign key behind <c>Stack.PinnedReleaseId</c> is the
/// backstop — this check exists for the message.
/// </para>
/// <para>
/// <c>Stack.LastDeployedReleaseId</c> and <c>DeployEvent.ReleaseId</c> are deliberately <em>not</em>
/// blockers: both are records of the past, both are <c>SET NULL</c>, and refusing a delete because
/// something once deployed the release would make pruning impossible.
/// </para>
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

        var pinnedBy = await db.Stacks.AsNoTracking()
            .Where(s => s.PinnedReleaseId == release.Id)
            .OrderBy(s => s.Name)
            .Select(s => s.Name)
            .ToListAsync(ct);
        if (pinnedBy.Count > 0) {
            return AppError.Conflict(
                $"Release '{release.Version}' is pinned by {Describe(pinnedBy)}. Move them to another "
                + "release, or clear their pin, before deleting it.");
        }

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

    /// <summary>
    /// The blocking stacks, named. Capped so a fleet-wide pin produces a readable sentence rather than a
    /// two-hundred-name wall — the count is what matters past the first few.
    /// </summary>
    private static string Describe(IReadOnlyList<string> stackNames) {
        const int shown = 5;
        var names = string.Join(", ", stackNames.Take(shown).Select(n => $"'{n}'"));
        return stackNames.Count <= shown
            ? $"stack(s) {names}"
            : $"{stackNames.Count} stacks, including {names}";
    }
}
