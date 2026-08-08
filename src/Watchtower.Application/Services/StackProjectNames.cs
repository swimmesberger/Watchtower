using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Guards the invariant that no two stacks share a compose project name.
/// </summary>
/// <remarks>
/// <para>
/// The compose project name is the identity Watchtower resolves containers by — the App API
/// (<c>/api/app/*</c>) finds a caller's own containers through the <c>com.docker.compose.project</c>
/// label, and the update checker and reverse-proxy reconciler do the same. Two stacks sharing one
/// project name therefore share containers: one stack's App API token would read the other's status
/// and logs. Matching is case-insensitive because Docker Compose treats project names that way, and
/// because the default project name is derived by lowercasing the stack name — so <c>Acme</c> and
/// <c>acme</c> would otherwise collide.
/// </para>
/// <para>
/// This is enforced on write rather than by a unique index. A <c>CREATE UNIQUE INDEX</c> migration
/// would fail at startup on any existing database that already contains colliding rows, and bricking
/// an upgrade is worse than the latent collision — which already breaks deploys for those stacks
/// independently of this check.
/// </para>
/// </remarks>
public static class StackProjectNames {
    /// <summary>
    /// Reports whether another stack already uses <paramref name="projectName"/>, ignoring case.
    /// </summary>
    /// <param name="db">Database context to query.</param>
    /// <param name="projectName">Resolved compose project name to test.</param>
    /// <param name="excludeStackId">Stack to exclude from the check (the one being updated); null when creating.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the name is already taken by a different stack.</returns>
    public static Task<bool> IsTakenAsync(
        WatchtowerDbContext db, string projectName, int? excludeStackId, CancellationToken ct) {
        // ToLower() (not StringComparison, which SQLite cannot translate) mirrors how the codebase
        // already does case-insensitive matching, e.g. the route-domain lookup.
        var lowered = projectName.ToLowerInvariant();
        return db.Stacks.AnyAsync(
            s => s.ComposeProjectName.ToLower() == lowered
                 && (excludeStackId == null || s.Id != excludeStackId),
            ct);
    }

    /// <summary>The validation message shown when a compose project name collides.</summary>
    /// <param name="projectName">The colliding name.</param>
    /// <returns>An operator-facing explanation naming the conflict and how to resolve it.</returns>
    public static string TakenMessage(string projectName) =>
        $"Compose project name '{projectName}' is already used by another stack (compared "
        + "case-insensitively). Two stacks sharing a project name would share containers. Choose a "
        + "different stack name, or set an explicit compose project name.";
}
