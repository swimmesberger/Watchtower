using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>
/// Pins a stack to one release, or clears the pin so it tracks latest again — rollback, canary and
/// "catch this tenant up" are all this one call (docs/products/design.md §Rollback and canary).
/// </summary>
/// <remarks>
/// <para>
/// <b>A pin is the opt-out from automation</b>, not merely a version choice: while it is set, release
/// fan-out, the scheduled window and every other automatic path skip this stack (design.md
/// §"Auto-deploy precedence", rule 2). Clearing it puts the stack back under the product's automation
/// and deploys latest, which is what makes "catch up" one action rather than two.
/// </para>
/// <para>
/// <b>The images are checked before anything is written.</b> A digest garbage-collected from the
/// registry would otherwise surface as a failed <c>compose pull</c> partway through a rollback; here it
/// is a <c>409</c> naming the reference, and the stack is untouched
/// (<see cref="ReleaseImageValidator"/>).
/// </para>
/// <para>
/// <b>Rollback rolls the code back too</b>, because a release stores its commit: the checkout, and with
/// it the compose file, entrypoints and migration files, travel with the images. What it cannot roll
/// back is the application's database — see the caveat in design.md §Rollback and canary.
/// </para>
/// </remarks>
[Handler("stacks.setRelease")]
public sealed class SetStackRelease(
    WatchtowerDbContext db,
    ReleaseImageValidator validator,
    DeployQueueService deployQueue,
    BackupQueueService backupQueue,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<SetStackRelease.Command, Result<SetStackRelease.Response>> {
    /// <summary>Audit action for pinning a stack to a release.</summary>
    public const string PinAction = "release.pin";

    /// <summary>Audit action for clearing a pin.</summary>
    public const string UnpinAction = "release.unpin";

    /// <param name="ReleaseId">The release to pin to, or null to clear the pin and track latest.</param>
    /// <param name="Deploy">
    /// Whether to deploy the resulting target immediately. True is the Save-and-deploy button and the
    /// default, because a pin nobody deploys leaves the stack running something the UI no longer claims
    /// it runs; false is Save, for an operator staging a change before a maintenance window.
    /// </param>
    /// <param name="BackupFirst">
    /// Take a backup before deploying, and deploy only if it succeeds — the roll-out dialog's "Back up
    /// each instance before deploying" (design.md §"Backups across tenants"). Ignored when
    /// <paramref name="Deploy"/> is false: there is nothing to guard.
    /// </param>
    public sealed record Command(int StackId, int? ReleaseId, bool Deploy = true, bool BackupFirst = false);

    /// <param name="Deployed">
    /// Whether a deploy was actually enqueued. False when the caller asked not to, and when the stack is
    /// stopped — a stopped stack is deliberately disabled (ADR-0025), and refusing the whole call would
    /// make "pin it, then start it" impossible.
    /// </param>
    /// <param name="DeployEventId">The tracking event, when one was enqueued.</param>
    /// <param name="BackupEventId">
    /// The pre-deploy backup's tracking event, when one was enqueued. Its presence with
    /// <paramref name="Deployed"/> false is the "backing up first" state: the deploy is chained to this
    /// run and will be enqueued when — and only when — it succeeds.
    /// </param>
    public sealed record Response(StackDto Stack, bool Deployed, int? DeployEventId, int? BackupEventId = null);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var stack = await db.Stacks
            .Include(s => s.UpdateCheck)
            .Include(s => s.Product)
            .Include(s => s.Template)
            .Include(s => s.PinnedRelease)
            .Include(s => s.LastDeployedRelease)
            .FirstOrDefaultAsync(s => s.Id == command.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        var before = stack.PinnedRelease is { } previous ? $"{previous.Version} (#{previous.Id})" : "latest";

        Release? target = null;
        if (command.ReleaseId is { } releaseId) {
            // Pinning a Git-mode product would write a value nothing reads: the resolver answers null
            // before it ever looks at the pin, so the stack would keep deploying branch heads while the
            // UI showed it pinned. Unpinning stays allowed below — an operator who reverted a product to
            // Git mode has to be able to clear the pins that survived it.
            if (stack.Product!.ReleaseMode != ProductReleaseMode.Releases) {
                return AppError.Conflict(
                    $"Product '{stack.Product.Name}' is in Git mode, so its stacks deploy the branch "
                    + "head rather than a release. Switch it to release mode first.");
            }

            target = await db.Releases.AsNoTracking()
                .Include(r => r.Images)
                .FirstOrDefaultAsync(r => r.Id == releaseId, ct);
            if (target is null)
                return AppError.NotFound($"Release {releaseId} not found.");
            // Not merely a sanity check: a release of another product pins digests this stack's compose
            // file will never match, so the stack would deploy unpinned and look pinned.
            if (target.ProductId != stack.ProductId) {
                return AppError.Validation(
                    $"Release '{target.Version}' belongs to a different product than stack "
                    + $"'{stack.Name}'.");
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

        stack.PinnedReleaseId = target?.Id;
        await db.SaveChangesAsync(ct);
        // Re-read for the response rather than hand-patching the navigation: the target was loaded
        // without tracking, so the change tracker fixed the navigation up against a principal it never
        // saw. One extra query on an operator action, and the DTO is provably what the database now says.
        var saved = await LoadForResponseAsync(stack.Id, ct) ?? stack;

        var after = target is null ? "latest" : $"{target.Version} (#{target.Id})";
        await audit.RecordAsync(
            StackLifecycle.AuditCategory, target is null ? UnpinAction : PinAction, stack.Name,
            $"release {before} → {after}",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        // A stopped stack is disabled, not misconfigured: the pin is recorded and the deploy is simply
        // not enqueued, so starting it later applies the pin like any other deploy.
        var deploy = command.Deploy && stack.DesiredState != StackDesiredState.Stopped;
        if (deploy && command.BackupFirst) {
            // Backup first, deploy on success. The deploy is not enqueued here at all — the chain
            // enqueues it, so a failed backup leaves nothing to cancel and the response can honestly say
            // nothing is deploying yet.
            var backup = backupQueue.Enqueue(
                stack.Id, BackupTriggers.PreDeploy,
                BackupChainStep.ForDeploy(stack.Id, DeployTriggers.ReleaseManual));
            return new Response(
                StackMapping.ToDto(saved, saved.UpdateCheck), Deployed: false, DeployEventId: null,
                BackupEventId: backup.BackupEventId);
        }
        var deployEventId = deploy
            ? deployQueue.Enqueue(stack.Id, DeployTriggers.ReleaseManual).DeployEventId
            : (int?)null;

        return new Response(StackMapping.ToDto(saved, saved.UpdateCheck), deploy, deployEventId);
    }

    /// <summary>The stack with everything <see cref="StackMapping.ToDto"/> projects, after the write.</summary>
    private Task<Stack?> LoadForResponseAsync(int stackId, CancellationToken ct) =>
        db.Stacks.AsNoTracking()
            .Include(s => s.UpdateCheck)
            .Include(s => s.Product)
            .Include(s => s.Template)
            .Include(s => s.PinnedRelease)
            .Include(s => s.LastDeployedRelease)
            .FirstOrDefaultAsync(s => s.Id == stackId, ct);
}
