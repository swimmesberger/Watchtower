namespace Watchtower.Application.Services;

/// <summary>
/// Pure retention policy for one stack's backup directory (ADR-0016 §5): age limit + count limit,
/// applied to the file names alone. Files whose name does not parse as a Watchtower backup are
/// ignored entirely, and the newest backup is never selected — a misconfigured retention can not
/// delete the backup that was just written.
/// </summary>
public static class BackupRetention {
    /// <summary>
    /// The subset of <paramref name="fileNames"/> retention deletes, given the limits. Zero for
    /// either limit disables that limit.
    /// </summary>
    public static IReadOnlyList<string> SelectDeletions(
        IReadOnlyList<string> fileNames, DateTimeOffset nowUtc, int retentionDays, int retentionMaxCount) {
        var backups = fileNames
            .Select(name => (Name: name, Timestamp: BackupNaming.ParseTimestamp(name)))
            .Where(x => x.Timestamp is not null)
            .OrderByDescending(x => x.Timestamp)
            .ToList();
        if (backups.Count <= 1) return [];

        var delete = new List<string>();
        for (var i = 1; i < backups.Count; i++) { // i = 0 is the newest — always kept
            var tooOld = retentionDays > 0 && backups[i].Timestamp < nowUtc.AddDays(-retentionDays);
            var overCount = retentionMaxCount > 0 && i >= retentionMaxCount;
            if (tooOld || overCount) delete.Add(backups[i].Name);
        }
        return delete;
    }
}
