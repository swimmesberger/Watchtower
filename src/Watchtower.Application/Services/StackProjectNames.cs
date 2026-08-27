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
    /// Validates a resolved compose project name against every other stack and against the project
    /// Watchtower itself runs under.
    /// </summary>
    /// <param name="db">Database context to query.</param>
    /// <param name="selfProjects">Resolver for Watchtower's own (reserved) project name.</param>
    /// <param name="projectName">Resolved compose project name to test.</param>
    /// <param name="excludeStackId">Stack to exclude from the check (the one being updated); null when creating.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="stackName">
    /// The stack's display name, when the caller has one. A plain stack's backup directory is derived
    /// from it (<see cref="BackupNaming.StackDirectory"/>), so the one name that has to be refused here
    /// as well is the one that would land its archives in Watchtower's own backup directory — where
    /// retention would then prune the instance's dumps and the stack's archives as one set (ADR-0027).
    /// Tenant stacks are unaffected: their directory is a level deeper, under the product.
    /// </param>
    /// <returns>An operator-facing error message, or null when the name is free to use.</returns>
    public static async Task<string?> ValidateAsync(
        WatchtowerDbContext db, SelfProjectNameProvider selfProjects,
        string projectName, int? excludeStackId, CancellationToken ct, string? stackName = null) {
        if (stackName is not null && BackupNaming.IsReserved(stackName))
            return $"Stack name '{stackName.Trim()}' is reserved: it is where Watchtower keeps the "
                   + "backups of its own database, and a stack backing up alongside them would share "
                   + "their retention. Choose a different stack name.";

        if (await selfProjects.IsReservedAsync(projectName, ct))
            return $"Compose project name '{projectName}' is reserved: it is the project Watchtower "
                   + "itself runs under. A stack sharing it would expose Watchtower's own containers "
                   + "through the App API. Choose a different stack name, or set an explicit compose "
                   + "project name.";

        if (await IsTakenAsync(db, projectName, excludeStackId, ct))
            return $"Compose project name '{projectName}' is already used by another stack (compared "
                   + "case-insensitively). Two stacks sharing a project name would share containers. "
                   + "Choose a different stack name, or set an explicit compose project name.";

        return null;
    }

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
        // ToLower() on both sides rather than StringComparison, which EF cannot translate. Unlike the
        // route domain, this column is not stored normalized (a compose project name is shown back to the
        // operator as they typed it), so the expression index ix_stacks_compose_project_name_lower —
        // added by the initial migration — is what keeps this an index lookup.
        var lowered = projectName.ToLowerInvariant();
        return db.Stacks.AnyAsync(
            s => s.ComposeProjectName.ToLower() == lowered
                 && (excludeStackId == null || s.Id != excludeStackId),
            ct);
    }
}
