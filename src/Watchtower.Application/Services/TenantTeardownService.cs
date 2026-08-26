using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Modules.Tenancy;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>Why a tenant could not be torn down, or that it was.</summary>
public enum TenantTeardownStatus {
    /// <summary>Containers are down and the stack row (with everything cascading off it) is gone.</summary>
    Removed,
    /// <summary>No tenant with that slug exists under that template.</summary>
    TenantNotFound,
    /// <summary>A deploy is queued or running for the tenant; tearing down under it would race the deploy.</summary>
    DeployActive,
    /// <summary>Compose could not bring the project down. Nothing was deleted — the call is retryable.</summary>
    ComposeDownFailed,
}

/// <summary>Outcome of <see cref="TenantTeardownService.TeardownAsync"/>.</summary>
/// <param name="Status">Whether the tenant was removed, and why not when it wasn't.</param>
/// <param name="Slug">
/// The normalized slug the teardown acted on, so callers echo the stored identifier rather than
/// whatever casing or padding the request happened to use. Null when the slug was unusable.
/// </param>
/// <param name="Error">Caller-facing message; null on success. Never carries command output.</param>
public sealed record TenantTeardownResult(
    TenantTeardownStatus Status, string? Slug = null, string? Error = null);

/// <summary>
/// Deprovisions one tenant of a <see cref="Entities.StackTemplate"/>: stops and removes its containers,
/// deletes its stack row, and reloads the reverse proxy so the tenant's domain stops being served.
/// </summary>
/// <remarks>
/// <para>
/// The order is the whole design. (1) A queued or running deploy is refused, because a deploy that is
/// recreating containers while teardown removes them leaves an orphaned project behind and a deploy
/// event pointing at a stack that no longer exists. (2) <c>docker compose down</c> runs <em>before</em>
/// the row is deleted: the compose project name lives on that row, so deleting first would leave
/// containers running with no record of what they belong to. If compose fails the teardown aborts right
/// there — containers up and row intact is a state the caller can simply retry. (3) The row is deleted,
/// cascading its routes, env vars and deploy events. (4) Only then is Caddy reloaded, so it stops
/// serving a route whose backend is already gone (the same post-delete reload
/// <c>proxy.deleteRoute</c> performs).
/// </para>
/// <para>
/// The deploy check is a check, not a lock, and the window it leaves is worth naming precisely. A deploy
/// enqueued just after the check passes starts its worker immediately, and that worker reads the stack
/// row exactly once, at entry, before a clone that takes seconds
/// (<see cref="DeployQueueService"/>'s <c>ExecuteDeployAsync</c>). If teardown completes during that
/// clone the worker carries on against its in-memory copy and brings the project back up — leaving
/// containers and an ingress network for a stack row that no longer exists, which nothing later
/// reconciles away. Only a deploy that starts <em>after</em> the row is gone fails itself. Closing the
/// window needs a lock or a tombstone on the stack, which is more machinery than the exposure justifies:
/// it takes an operator (or a granted manager) deleting a tenant in the same instant something else
/// redeploys it, and the residue is removable with one <c>docker compose down</c>.
/// </para>
/// <para>
/// Everything after a successful compose-down runs uncancellably — once the containers are destroyed the
/// row must follow, or the tenant would be left as a row whose containers no longer exist, waiting to be
/// recreated by the next deploy.
/// </para>
/// </remarks>
/// <param name="db">Scoped Watchtower database context.</param>
/// <param name="compose">Docker Compose CLI wrapper used to bring the project down.</param>
/// <param name="proxy">Reverse-proxy reconciler (the active provider), reloaded once the route rows are gone.</param>
/// <param name="logger">Logger; compose output is logged here rather than returned to the caller.</param>
public sealed class TenantTeardownService(
    WatchtowerDbContext db,
    ComposeCliService compose,
    IProxyProvider proxy,
    ILogger<TenantTeardownService> logger) {
    /// <summary>
    /// Tears down the tenant identified by <paramref name="templateId"/> and <paramref name="slug"/>.
    /// </summary>
    /// <param name="templateId">Template the tenant belongs to; the lookup is always constrained by it.</param>
    /// <param name="slug">Tenant slug, normalized here the same way it was normalized at creation.</param>
    /// <param name="removeVolumes">When true the project's named volumes are deleted too — an irreversible data wipe.</param>
    /// <param name="ct">Cancellation token; honoured only up to the point the containers are destroyed.</param>
    /// <returns>Whether the tenant was removed, and why not when it wasn't.</returns>
    public async Task<TenantTeardownResult> TeardownAsync(
        int templateId, string? slug, bool removeVolumes, CancellationToken ct) {
        var (failure, tenant) = await ResolveAsync(templateId, slug, ct);
        if (failure is not null || tenant is null) return failure!;
        var normalized = tenant.Slug;

        var (exitCode, output) = await compose.DownProjectAsync(tenant.ComposeProjectName, removeVolumes, ct);
        if (exitCode != 0) {
            // The output can name images, registries and mount paths, so it goes to the operator's log
            // rather than into a response body on a surface a tenant-managing stack can read.
            logger.LogWarning(
                "docker compose down for project {Project} exited {ExitCode}; tenant {Slug} was not deleted. Output: {Output}",
                tenant.ComposeProjectName, exitCode, normalized, output);
            return new TenantTeardownResult(TenantTeardownStatus.ComposeDownFailed, normalized,
                $"Tenant '{normalized}' could not be stopped; nothing was deleted.");
        }

        // Past the destructive point: the containers are gone, so the row has to go with them whatever
        // the caller does with its request.
        await db.Stacks.Where(s => s.Id == tenant.StackId).ExecuteDeleteAsync(CancellationToken.None);
        // The route rows cascaded away with the stack; reload so the proxy stops serving the dead domain.
        await proxy.ApplyAsync(CancellationToken.None);

        return new TenantTeardownResult(TenantTeardownStatus.Removed, normalized);
    }

    /// <summary>The tenant a teardown would act on, once it is known to exist and to be idle.</summary>
    /// <param name="StackId">Its stack id — what a chained final backup is enqueued against.</param>
    /// <param name="Slug">The normalized slug, i.e. the stored identifier.</param>
    /// <param name="ComposeProjectName">The project <c>compose down</c> is run for.</param>
    public sealed record ResolvedTenant(int StackId, string Slug, string ComposeProjectName);

    /// <summary>
    /// The two checks that must pass before anything is destroyed — the tenant exists, and no deploy is
    /// in flight — separated from the teardown so the <em>final-backup</em> path can run them at the
    /// moment the operator asks rather than minutes later when the backup finishes.
    /// </summary>
    /// <remarks>
    /// The deploy check is re-run by <see cref="TeardownAsync"/> when the chain eventually calls it, so
    /// a deploy that starts while the final backup runs still refuses the teardown. Checking here as
    /// well is what turns "your removal was aborted 4 minutes later" into an immediate refusal in the
    /// dialog for the case that is knowable immediately.
    /// </remarks>
    /// <param name="templateId">Template the tenant belongs to.</param>
    /// <param name="slug">Requested slug; normalized here.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The failure to report, or the resolved tenant. Exactly one is non-null.</returns>
    public async Task<(TenantTeardownResult? Failure, ResolvedTenant? Tenant)> ResolveAsync(
        int templateId, string? slug, CancellationToken ct) {
        // An unusable slug cannot name a stored tenant — normalization is what created the stored value.
        var normalized = TenancyMapping.NormalizeSlug(slug);
        if (normalized is null)
            return (NotFound(slug), null);

        var tenant = await db.Stacks.AsNoTracking()
            .Where(s => s.TemplateId == templateId && s.TenantSlug == normalized)
            .Select(s => new { s.Id, s.ComposeProjectName })
            .FirstOrDefaultAsync(ct);
        if (tenant is null)
            return (NotFound(normalized), null);

        var deployActive = await db.DeployEvents.AsNoTracking()
            .AnyAsync(e => e.StackId == tenant.Id && (e.Status == "running" || e.Status == "queued"), ct);
        if (deployActive) {
            return (new TenantTeardownResult(TenantTeardownStatus.DeployActive, normalized,
                $"Tenant '{normalized}' has a deploy in progress; retry once it has finished."), null);
        }

        return (null, new ResolvedTenant(tenant.Id, normalized, tenant.ComposeProjectName));
    }

    private static TenantTeardownResult NotFound(string? slug) =>
        new(TenantTeardownStatus.TenantNotFound, Error: $"Tenant '{slug}' not found for this template.");
}
