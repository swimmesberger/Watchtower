using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Scoped key/value access over the <c>app_settings</c> table. Replaces the raw-SQLite
/// settings repository from the pre-Elarion codebase. Values are plain strings; callers
/// convert to the requested type. All operations are synchronous — the table is tiny and
/// only touched by the (infrequent) self-update flow.
/// </summary>
public sealed class SettingsStore(WatchtowerDbContext db) {
    /// <summary>Returns the stored value for <paramref name="key"/>, or null if not set.</summary>
    public string? Get(string key) =>
        db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefault();

    /// <summary>Returns every setting as a dictionary (single query).</summary>
    public Dictionary<string, string> GetAll() =>
        db.AppSettings.AsNoTracking().ToDictionary(s => s.Key, s => s.Value);

    /// <summary>
    /// Persists <paramref name="value"/> under <paramref name="key"/> (insert or replace).
    /// Passing null removes the key.
    /// </summary>
    public void Set(string key, string? value) {
        var existing = db.AppSettings.FirstOrDefault(s => s.Key == key);
        if (value is null) {
            if (existing is not null) {
                db.AppSettings.Remove(existing);
                db.SaveChanges();
            }
            return;
        }
        if (existing is null)
            db.AppSettings.Add(new AppSetting { Key = key, Value = value });
        else
            existing.Value = value;
        db.SaveChanges();
    }

    /// <summary>Applies several key/value writes (null value = delete) in a single SaveChanges.</summary>
    public void SetMany(IEnumerable<KeyValuePair<string, string?>> pairs) {
        var updates = pairs.ToList();
        var keys = updates.Select(p => p.Key).ToHashSet();
        var existing = db.AppSettings.Where(s => keys.Contains(s.Key)).ToDictionary(s => s.Key);
        foreach (var (key, value) in updates) {
            existing.TryGetValue(key, out var row);
            if (value is null) {
                if (row is not null) db.AppSettings.Remove(row);
            } else if (row is null) {
                db.AppSettings.Add(new AppSetting { Key = key, Value = value });
            } else {
                row.Value = value;
            }
        }
        db.SaveChanges();
    }
}
