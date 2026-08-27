using System.Text.Json;
using System.Text.Json.Serialization;
using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>Where one stack has got to in the post-restore revival (ADR-0027 §6).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RevivalStatus>))]
public enum RevivalStatus {
    /// <summary>Nothing has been done for it yet.</summary>
    Pending,

    /// <summary>Being deployed from git.</summary>
    Deploying,

    /// <summary>Deployed; its newest archive is being restored into its volumes.</summary>
    Restoring,

    /// <summary>Deployed and, where there was an archive, restored.</summary>
    Done,

    /// <summary>The deploy or the restore failed; the run's own event says why.</summary>
    Failed,

    /// <summary>Dismissed by the operator, who is handling this one themselves.</summary>
    Skipped,
}

/// <summary>One stack on the recovery checklist.</summary>
/// <param name="StackId">Its id in the restored database.</param>
/// <param name="Name">Its name, as the checklist shows it.</param>
/// <param name="Status">Where it has got to.</param>
/// <param name="Detail">What last happened to it, in a sentence.</param>
/// <param name="DeployEventId">The deploy this revival started, once it has one.</param>
/// <param name="BackupEventId">The restore this revival started, once it has one.</param>
public sealed record RevivalStack(
    int StackId,
    string Name,
    RevivalStatus Status,
    string? Detail = null,
    int? DeployEventId = null,
    int? BackupEventId = null);

/// <summary>
/// The checklist an operator works through after an instance restore: every stack the restored database
/// knows about, to be redeployed from git and then restored from its newest archive (ADR-0027 §6).
/// </summary>
/// <param name="RestoredAtUtc">When the restore completed.</param>
/// <param name="SourceInstance">The instance the bundle came from.</param>
/// <param name="Dismissed">Whether the operator has put the checklist away.</param>
/// <param name="Stacks">The stacks, in the order the checklist shows them.</param>
public sealed record StackRevivalState(
    DateTimeOffset RestoredAtUtc,
    string SourceInstance,
    bool Dismissed,
    IReadOnlyList<RevivalStack> Stacks) {
    /// <summary>
    /// A Global settings row rather than a table: there is at most one of these, it has to survive the
    /// restart the restore itself causes, and giving it a migration would be a schema change carried by
    /// every instance that never restores anything.
    /// </summary>
    public const string SettingPath = WatchtowerSettingPaths.RestoreRecovery;

    /// <summary>Reads the checklist, or null when there is none.</summary>
    public static async Task<StackRevivalState?> LoadAsync(
        ISettingsManager settings, CancellationToken ct) {
        var stored = await settings.GetStringAsync(SettingPath, SettingsScope.Global, ct);
        if (string.IsNullOrWhiteSpace(stored)) return null;
        try {
            return JsonSerializer.Deserialize<StackRevivalState>(stored, BackupBundle.JsonOptions);
        } catch (JsonException) {
            // Written by this build alone; unreadable means a hand-edit or a downgrade. Treated as
            // absent rather than fatal — the checklist is a convenience, not a source of truth.
            return null;
        }
    }

    /// <summary>Writes the checklist back.</summary>
    public Task SaveAsync(ISettingsManager settings, CancellationToken ct) =>
        settings.SetStringAsync(
            SettingPath, JsonSerializer.Serialize(this, BackupBundle.JsonOptions),
            SettingsScope.Global, expectedVersion: null, ct).AsTask();

    /// <summary>Removes it, once the operator is done with it.</summary>
    public static Task ClearAsync(ISettingsManager settings, CancellationToken ct) =>
        settings.RemoveAsync(SettingPath, SettingsScope.Global, expectedVersion: null, ct).AsTask();

    /// <summary>
    /// Seeds the checklist from the database a restore has just brought in. The stacks come from that
    /// database rather than from the bundle's manifest, because the ids the checklist has to act on are
    /// the restored ones.
    /// </summary>
    internal static async Task SeedAsync(
        ISettingsManager settings, RestoreProgress progress, WatchtowerDbContext db, CancellationToken ct) {
        var stacks = await db.Stacks.AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name })
            .ToListAsync(ct);
        var state = new StackRevivalState(
            DateTimeOffset.UtcNow, progress.SourceInstance, Dismissed: false,
            [.. stacks.Select(s => new RevivalStack(s.Id, s.Name, RevivalStatus.Pending))]);
        await state.SaveAsync(settings, ct);
    }

    /// <summary>The checklist with one stack replaced, leaving the rest as they were.</summary>
    public StackRevivalState With(RevivalStack stack) => this with {
        Stacks = [.. Stacks.Select(s => s.StackId == stack.StackId ? stack : s)],
    };
}
