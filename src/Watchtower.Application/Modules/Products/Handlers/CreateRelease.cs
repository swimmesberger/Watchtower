using Elarion.Abstractions.Identity;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// Records a release by hand — the way to adopt the release model before the CI workflow is wired, and
/// the way to re-record one after a mistake.
/// </summary>
/// <remarks>
/// The same <see cref="ReleaseIntakeService"/> the webhook runs on: identical validation, identical
/// tag→digest resolution, identical fingerprint. What this path adds is an actor on the audit row, and
/// what it leaves out is the branch — a manual entry is by definition about the product's own branch,
/// so there is nothing to disagree about.
/// </remarks>
[Handler("products.createRelease")]
public sealed class CreateRelease(ReleaseIntakeService intake, AuditLog audit, ICurrentUser currentUser)
    : IHandler<CreateRelease.Command, Result<CreateRelease.Response>> {
    /// <param name="Version">The display label; unique per product.</param>
    /// <param name="CommitSha">The 40-hex commit this build came from, when there is one.</param>
    /// <param name="Images">Image references, each <c>repo:tag</c> or <c>repo@sha256:…</c>.</param>
    public sealed record Command(
        int ProductId,
        string Version,
        IReadOnlyList<string> Images,
        string? CommitSha = null,
        string? Notes = null);

    public sealed record Response(ReleaseDetailDto Release);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var result = await intake.PublishAsync(
            new ReleaseIntakeRequest(
                command.ProductId,
                command.Images ?? [],
                Release.ViaManual,
                CommitSha: command.CommitSha,
                Version: command.Version,
                Notes: command.Notes,
                Actor: await audit.ActorAsync(currentUser, ct)),
            ct);

        if (result.Release is { } release) {
            // A replay answers with the release that already existed rather than an error: recording
            // the same build twice is not a mistake worth refusing, and the second call changed nothing.
            return new Response(ProductMapping.ToDetailDto(release, result.ProductName!));
        }

        return result.Status switch {
            ReleaseIntakeStatus.ProductNotFound => AppError.NotFound(result.Error!),
            ReleaseIntakeStatus.VersionConflict => AppError.Conflict(result.Error!),
            // The request was fine and the world was not — a retry of the identical call may succeed.
            ReleaseIntakeStatus.RegistryUnavailable => AppError.BusinessRule(result.Error!),
            _ => AppError.Validation(result.Error!),
        };
    }
}
