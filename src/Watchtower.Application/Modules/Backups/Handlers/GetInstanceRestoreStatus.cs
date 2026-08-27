using Elarion.Abstractions.Authorization;
using Elarion.Settings;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Backups.Handlers;

/// <summary>
/// What the restore wizard needs to decide what to show (ADR-0027): whether this instance looks brand
/// new, whether a bundle is waiting to be restored and what it holds, how the last restore ended, and
/// whether there is a recovery checklist still to work through.
/// </summary>
[Handler("backups.getRestoreStatus")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class GetInstanceRestoreStatus(
    InstanceRestoreService restore,
    InstanceRestoreStaging staging,
    RestoreCompletionService completion,
    ISettingsManager settings)
    : IHandler<GetInstanceRestoreStatus.Query, Result<GetInstanceRestoreStatus.Response>> {
    public sealed record Query;

    /// <param name="FreshInstance">
    /// No stacks, no deploys and one account — a Watchtower nobody has used yet. A hint for what to
    /// offer, never a permission: the restore is gated on being an admin either way.
    /// </param>
    /// <param name="Staged">The uploaded bundle, checked against this instance, or null.</param>
    /// <param name="LastOutcome">How the last restore this instance attempted ended.</param>
    /// <param name="LastError">Why, when it failed.</param>
    /// <param name="RecoveryPending">Whether a post-restore checklist is still open.</param>
    public sealed record Response(
        bool FreshInstance,
        RestoreValidationDto? Staged,
        string LastOutcome,
        string? LastError,
        bool RecoveryPending);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        RestoreValidationDto? staged = null;
        if (staging.Current is { } current)
            staged = RestoreValidationDto.From(await restore.ValidateAsync(current, ct));

        var recovery = await StackRevivalState.LoadAsync(settings, ct);
        return new Response(
            FreshInstance: await restore.IsFreshAsync(ct),
            Staged: staged,
            LastOutcome: completion.LastOutcome.ToString().ToLowerInvariant(),
            LastError: completion.LastError,
            RecoveryPending: recovery is { Dismissed: false });
    }
}
