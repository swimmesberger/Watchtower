using System.Text.Json.Serialization;

namespace Watchtower.Application.Services;

/// <summary>Why a staged bundle cannot be restored into this instance, or a caveat about doing so.</summary>
/// <param name="Code">A stable key the UI can branch on, e.g. <c>key-protection-secret</c>.</param>
/// <param name="Message">The operator-facing sentence, which always names what to do about it.</param>
public sealed record RestoreFinding(string Code, string Message);

/// <summary>
/// What a staged bundle is, and whether this instance can restore it (ADR-0027 §5). Produced before
/// anything is touched — the whole point is that the refusals happen while the instance is still intact.
/// </summary>
/// <param name="CanRestore">False when <paramref name="Blocking"/> is non-empty.</param>
/// <param name="Blocking">Reasons the restore is refused outright.</param>
/// <param name="Warnings">Things worth knowing that do not stop it.</param>
/// <param name="InstanceName">The instance the bundle came from.</param>
/// <param name="AppVersion">The Watchtower build that wrote it.</param>
/// <param name="CreatedAtUtc">When it was written.</param>
/// <param name="StackCount">How many stacks it carries archives for.</param>
/// <param name="MissingStackCount">How many stacks it describes but has no archive for.</param>
/// <param name="StackNames">Their names, so the confirmation dialog can say what is about to arrive.</param>
public sealed record RestoreValidation(
    bool CanRestore,
    IReadOnlyList<RestoreFinding> Blocking,
    IReadOnlyList<RestoreFinding> Warnings,
    string InstanceName,
    string AppVersion,
    DateTimeOffset CreatedAtUtc,
    int StackCount,
    int MissingStackCount,
    IReadOnlyList<string> StackNames);

/// <summary>
/// The marker an in-progress restore leaves in Watchtower's own container, so the process that comes
/// back after the coordinator has stopped and started it can say what happened (ADR-0027 §5).
/// </summary>
/// <remarks>
/// It lives in the container's filesystem rather than the database precisely because the database is
/// what is being replaced. The container is stopped and started, never recreated, so the file survives —
/// that is the reason the restore coordinator does not use the self-update coordinator's recreate.
/// </remarks>
/// <param name="Nonce">
/// A random value also written into the database being replaced. After the restart its <em>absence</em>
/// from the database is the proof that the replay committed: no other event can remove it.
/// </param>
/// <param name="StartedAtUtc">When the restore was kicked off.</param>
/// <param name="SourceInstance">The instance name the bundle came from, for the audit row.</param>
/// <param name="CoordinatorId">The coordinator container, so its exit code and logs can be read back.</param>
/// <param name="StackNames">The stacks the bundle carried, for the recovery checklist.</param>
public sealed record RestoreProgress(
    string Nonce,
    DateTimeOffset StartedAtUtc,
    string SourceInstance,
    string? CoordinatorId,
    IReadOnlyList<string> StackNames);

/// <summary>How the last restore this instance attempted ended.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RestoreOutcome>))]
public enum RestoreOutcome {
    /// <summary>No restore has been attempted, or the result has been cleared.</summary>
    None,

    /// <summary>The replay committed and this process is running on the restored database.</summary>
    Succeeded,

    /// <summary>The coordinator did not replace the database; the instance is as it was.</summary>
    Failed,
}
