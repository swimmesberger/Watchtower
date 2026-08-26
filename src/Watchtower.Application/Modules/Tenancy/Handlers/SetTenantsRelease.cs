using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>
/// Sets one version policy for a whole tenant fleet: pins every current tenant to a release — or clears
/// every pin — <em>and</em> stores the choice as the template's default for the tenants that do not
/// exist yet (docs/products/design.md §"Rollback and canary").
/// </summary>
/// <remarks>
/// <para>
/// <b>Both halves, in one call, deliberately.</b> Writing only the tenants would leave the next tenant
/// provisioned tomorrow on a different version from the fleet it joins; writing only the default would
/// change nothing anybody is running. The two together are what "the fleet is on 1.4.0" means, and the
/// single call is what keeps them from drifting.
/// </para>
/// <para>
/// <b>Individual tenants can still disagree afterwards.</b> The pin is copied onto each tenant rather
/// than read through the template, so <c>stacks.setRelease</c> on one tenant is a per-tenant hotfix that
/// does not leave the fleet default — and this call is how the fleet is brought back together. That
/// asymmetry is the whole reason <see cref="StackTemplate.DefaultPinnedReleaseId"/> is copied at
/// provisioning rather than referenced.
/// </para>
/// <para>
/// <b>The pre-flight is <c>stacks.setRelease</c>'s</b> (<see cref="ReleaseImageValidator"/>), and it
/// matters more here: a digest garbage-collected from the registry would otherwise fail at
/// <c>compose pull</c> on every tenant of the fleet, one after another, halfway through a rollback.
/// One HEAD per image before anything is written turns that into a refusal that changed nothing.
/// </para>
/// <para>
/// <b>The two writes are one transaction, and the deploys are outside it.</b> The tenants' pins and the
/// template default land together or not at all — a half-applied fleet whose default names a different
/// release is the exact state this handler exists to prevent. The enqueues come after the commit, for
/// the reason invariant 9 spells out for release intake: a deploy resolves its release on another
/// thread through another connection, so anything enqueued from inside an open transaction would race
/// the data it depends on.
/// </para>
/// <para>
/// <b>Nothing waits.</b> The deploys are enqueued, and the instance-wide gate
/// (<c>Watchtower:MaxConcurrentDeploys</c>) is what bounds a 200-tenant herd; this returns as fast as it
/// can write the pins.
/// </para>
/// </remarks>
[Handler("templates.setTenantsRelease")]
public sealed class SetTenantsRelease(
    WatchtowerDbContext db,
    ReleaseImageValidator validator,
    DeployQueueService deployQueue,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<SetTenantsRelease.Command, Result<SetTenantsRelease.Response>> {
    /// <summary>Audit action for a fleet-wide pin or unpin (docs/products/design.md §Audit).</summary>
    public const string AuditAction = "release.pin.bulk";

    /// <param name="ReleaseId">The release to pin the fleet to, or null to clear every pin and track latest.</param>
    /// <param name="Deploy">
    /// Whether to enqueue each tenant's deploy immediately. Defaults to false, unlike
    /// <c>stacks.setRelease</c>: one stack redeploying is an operator watching one deploy, and a fleet
    /// redeploying is an event to opt into.
    /// </param>
    public sealed record Command(int TemplateId, int? ReleaseId, bool Deploy = false);

    public sealed record Response(SetTenantsReleaseResultDto Result);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var template = await db.StackTemplates
            .Include(t => t.Product)
            .Include(t => t.DefaultPinnedRelease)
            .FirstOrDefaultAsync(t => t.Id == command.TemplateId, ct);
        if (template is null)
            return AppError.NotFound($"Template {command.TemplateId} not found");

        var before = template.DefaultPinnedRelease is { } previous
            ? $"{previous.Version} (#{previous.Id})"
            : "latest";

        Release? target = null;
        if (command.ReleaseId is { } releaseId) {
            // The stacks.setRelease precedent: pinning a Git-mode product writes a value nothing reads,
            // because the resolver answers null before it ever looks at a pin. Clearing stays allowed
            // below, so a fleet whose product was reverted can always be freed.
            if (template.Product!.ReleaseMode != ProductReleaseMode.Releases) {
                return AppError.Conflict(
                    $"Product '{template.Product.Name}' is in Git mode, so its stacks deploy the branch "
                    + "head rather than a release. Switch it to release mode first.");
            }

            target = await db.Releases.AsNoTracking()
                .Include(r => r.Images)
                .FirstOrDefaultAsync(r => r.Id == releaseId, ct);
            if (target is null)
                return AppError.NotFound($"Release {releaseId} not found.");
            // A release of another product pins digests these tenants' compose file can never match, so
            // every one of them would deploy unpinned while the roster called them pinned.
            if (target.ProductId != template.ProductId) {
                return AppError.Validation(
                    $"Release '{target.Version}' belongs to a different product than template "
                    + $"'{template.Name}'.");
            }

            var validation = await validator.ValidateAsync(target, ct);
            switch (validation.Status) {
                case ReleaseImageCheck.Missing:
                    return AppError.Conflict(
                        $"Release '{target.Version}' cannot be deployed: its registry no longer has "
                        + $"{string.Join(", ", validation.Missing)}. Pin a release whose images are "
                        + "still published.");
                case ReleaseImageCheck.Unavailable:
                    return AppError.BusinessRule(
                        "Could not verify the images of release "
                        + $"'{target.Version}' — {string.Join(", ", validation.Unreachable)} did not "
                        + "answer. Nothing was changed; retry.");
            }
        }

        // The tenants, and whether each may be deployed. Read before the write so the deploy list is the
        // fleet the pin was applied to, and ordered by id so a fan-out drains in a stable order.
        var tenants = await db.Stacks.AsNoTracking()
            .Where(s => s.TemplateId == template.Id)
            .OrderBy(s => s.Id)
            .Select(s => new { s.Id, s.DesiredState })
            .ToListAsync(ct);

        // **Both halves commit together or neither does.** `ExecuteUpdateAsync` and `SaveChangesAsync`
        // are two statements, and without a transaction they are two *implicit* transactions: a failure
        // between them — a concurrency token on the template, a connection drop — would leave the
        // tenants pinned while the template default still named the old release, so the next tenant
        // provisioned would join a fleet it disagrees with. That is precisely the state this handler's
        // whole reason for existing is to prevent, and the remarks above promise it. The pattern is
        // `CreateTemplate`'s: open before the first write, commit after the last.
        //
        // One statement for the fleet rather than a tracked entity per tenant: nothing here needs the
        // stack objects, and 200 of them through the change tracker to set one column is the shape that
        // makes a fleet operation slow. The template default rides on the tracked entity beside it.
        await using (var tx = await db.Database.BeginTransactionAsync(ct)) {
            if (tenants.Count > 0) {
                await db.Stacks
                    .Where(s => s.TemplateId == template.Id)
                    .ExecuteUpdateAsync(
                        s => s.SetProperty(x => x.PinnedReleaseId, target == null ? null : target.Id), ct);
            }
            template.DefaultPinnedReleaseId = target?.Id;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var after = target is null ? "latest" : $"{target.Version} (#{target.Id})";
        await audit.RecordAsync(
            StackLifecycle.AuditCategory, AuditAction, template.Name,
            $"release {before} → {after}; {tenants.Count} tenant(s) and the template default",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        // A stopped tenant is disabled, not misconfigured (ADR-0025): its pin is written and its deploy
        // is skipped, exactly as stacks.setRelease treats a stopped stack.
        var deployEventIds = new List<int>();
        if (command.Deploy) {
            foreach (var tenant in tenants.Where(t => t.DesiredState != StackDesiredState.Stopped))
                deployEventIds.Add(deployQueue.Enqueue(tenant.Id, DeployTriggers.ReleaseManual).DeployEventId);
        }

        return new Response(new SetTenantsReleaseResultDto(
            tenants.Count, deployEventIds.Count, deployEventIds, TenancyMapping.ReleaseRef(target)));
    }
}
