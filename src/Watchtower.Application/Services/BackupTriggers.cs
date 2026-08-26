namespace Watchtower.Application.Services;

/// <summary>
/// The values <see cref="Entities.BackupEvent.TriggeredBy"/> takes. Constants for the same reason
/// <see cref="DeployTriggers"/> is: the column is free text and has been since ADR-0016, and what
/// matters is that every writer spells a trigger the same way — the history rows are filtered and read
/// by these words.
/// </summary>
public static class BackupTriggers {
    /// <summary>An operator pressed "Back up now".</summary>
    public const string Manual = "manual";

    /// <summary>The backup schedule's window opened for this stack (ADR-0018).</summary>
    public const string Schedule = "schedule";

    /// <summary>A restore run, which shares the queue and the event table with backups.</summary>
    public const string Restore = "restore";

    /// <summary><c>templates.backupAll</c> fanned a backup out to every tenant of a template.</summary>
    public const string TemplateAll = "template-backup-all";

    /// <summary>
    /// A backup taken because a deploy is about to happen and the operator asked for one first — the
    /// roll-out dialog's "Back up each instance before deploying". The deploy runs only if this
    /// succeeds (<see cref="BackupChainCoordinator"/>).
    /// </summary>
    public const string PreDeploy = "pre-deploy";

    /// <summary>
    /// The last backup of a tenant, taken because it is about to be removed. The teardown runs only if
    /// this succeeds; a failure aborts the removal (<see cref="BackupChainCoordinator"/>).
    /// </summary>
    public const string Final = "final";
}
