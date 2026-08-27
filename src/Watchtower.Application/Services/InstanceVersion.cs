using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// What build and what schema this instance is running — the two facts an instance archive records so a
/// restore can refuse a backup the target cannot read (ADR-0027).
/// </summary>
/// <remarks>
/// The pair is deliberate. The version string is for the operator ("this bundle came from 1.4.2"); the
/// migration id is what the decision is actually made on, because it is exact: a binary either knows a
/// migration or it does not, where comparing version strings only guesses at what that means. Migrations
/// roll forward only, so an archive whose last migration this binary has never heard of was written by a
/// newer Watchtower and must not be replayed into this one.
/// </remarks>
public static class InstanceVersion {
    /// <summary>
    /// The informational version of the Watchtower build, e.g. <c>1.4.2+abc1234</c>. Falls back to the
    /// assembly version, and finally to <c>unknown</c> — a manifest key that is never absent is easier to
    /// read than one that sometimes is.
    /// </summary>
    public static string App { get; } = Resolve();

    /// <summary>
    /// The last migration applied to <paramref name="db"/>, or null when the database has none yet.
    /// </summary>
    /// <param name="db">The context to ask.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<string?> LastMigrationAsync(WatchtowerDbContext db, CancellationToken ct) =>
        (await db.Database.GetAppliedMigrationsAsync(ct)).LastOrDefault();

    /// <summary>
    /// Whether this binary knows <paramref name="migrationId"/> — i.e. whether an archive stamped with it
    /// can be replayed here. A null id (an archive from a database with no migrations, or a manifest
    /// written before the key existed) is accepted: there is nothing to be newer than.
    /// </summary>
    /// <param name="db">The context whose model carries the known migrations.</param>
    /// <param name="migrationId">The migration id recorded in the archive.</param>
    public static bool Knows(WatchtowerDbContext db, string? migrationId) =>
        string.IsNullOrEmpty(migrationId)
        || db.Database.GetMigrations().Contains(migrationId, StringComparer.Ordinal);

    private static string Resolve() {
        var assembly = typeof(InstanceVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational;
        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
