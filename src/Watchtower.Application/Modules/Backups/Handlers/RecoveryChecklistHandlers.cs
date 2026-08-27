using Elarion.Abstractions.Authorization;
using Elarion.Abstractions.Identity;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// The post-restore recovery checklist (ADR-0027 §6): every stack the restored database knows about,
/// waiting to be redeployed from git and restored from its newest archive.
/// </summary>
[Handler("backups.getRecoveryChecklist")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class GetRecoveryChecklist(StackRevivalCoordinator revival)
    : IHandler<GetRecoveryChecklist.Query, Result<GetRecoveryChecklist.Response>> {
    public sealed record Query;

    /// <param name="Checklist">The checklist, or null when there is nothing to recover.</param>
    public sealed record Response(RecoveryChecklistDto? Checklist);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) =>
        new Response(await revival.LoadAsync(ct) is { } state ? RecoveryChecklistDto.From(state) : null);
}

/// <summary>
/// Revives one stack: deploy it from git, then restore its newest archive into the volumes that deploy
/// created (ADR-0027 §6). Runs to completion before returning — a single stack is one deploy and one
/// restore, both of which the UI already knows how to wait on.
/// </summary>
[Handler("backups.reviveStack")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ReviveStack(StackRevivalCoordinator revival, AuditLog audit, ICurrentUser currentUser)
    : IHandler<ReviveStack.Command, Result<ReviveStack.Response>> {
    public sealed record Command(int StackId);

    public sealed record Response(RecoveryStackDto Stack);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (await revival.ReviveAsync(command.StackId, ct) is not { } stack)
            return AppError.NotFound($"Stack {command.StackId} is not on the recovery checklist.");

        await audit.RecordAsync(
            BackupService.AuditCategory, "recovery.revive", stack.Name,
            $"{stack.Status.ToString().ToLowerInvariant()} — {stack.Detail}",
            success: stack.Status is not RevivalStatus.Failed,
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);
        return new Response(RecoveryStackDto.From(stack));
    }
}

/// <summary>
/// Revives every stack still pending or failed, one after another (ADR-0027 §6). A failure does not
/// stop the rest: the stacks are independent, and stopping at the first would leave the operator to
/// work out which of the others had been tried.
/// </summary>
[Handler("backups.reviveAll")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ReviveAllStacks(
    StackRevivalCoordinator revival, AuditLog audit, ICurrentUser currentUser)
    : IHandler<ReviveAllStacks.Command, Result<ReviveAllStacks.Response>> {
    public sealed record Command;

    /// <param name="Revived">How many stacks ended up fully back.</param>
    /// <param name="Checklist">The checklist as it now stands.</param>
    public sealed record Response(int Revived, RecoveryChecklistDto? Checklist);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var revived = await revival.ReviveAllAsync(ct);
        var checklist = await revival.LoadAsync(ct);
        await audit.RecordAsync(
            BackupService.AuditCategory, "recovery.revive", "all stacks",
            $"{revived} of {checklist?.Stacks.Count ?? 0} stack(s) deployed and restored",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);
        return new Response(revived, checklist is null ? null : RecoveryChecklistDto.From(checklist));
    }
}

/// <summary>Marks one stack as handled outside Watchtower, so "revive all" leaves it alone.</summary>
[Handler("backups.skipRecoveryStack")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class SkipRecoveryStack(StackRevivalCoordinator revival)
    : IHandler<SkipRecoveryStack.Command, Result<SkipRecoveryStack.Response>> {
    public sealed record Command(int StackId);

    public sealed record Response(RecoveryStackDto Stack);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) =>
        await revival.SkipAsync(command.StackId, ct) is { } stack
            ? new Response(RecoveryStackDto.From(stack))
            : AppError.NotFound($"Stack {command.StackId} is not on the recovery checklist.");
}

/// <summary>
/// Puts the checklist away. It is a prompt, not a record — what was restored and revived is in the audit
/// trail, which is where that question belongs.
/// </summary>
[Handler("backups.dismissRecovery")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class DismissRecovery(
    StackRevivalCoordinator revival, AuditLog audit, ICurrentUser currentUser)
    : IHandler<DismissRecovery.Command, Result<DismissRecovery.Response>> {
    public sealed record Command;

    public sealed record Response(bool Dismissed);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        await revival.DismissAsync(ct);
        await audit.RecordAsync(
            BackupService.AuditCategory, "recovery.dismiss", InstanceRestoreService.AuditTarget,
            "recovery checklist dismissed",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);
        return new Response(true);
    }
}
