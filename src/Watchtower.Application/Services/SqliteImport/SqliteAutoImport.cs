using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services.SqliteImport;

/// <summary>
/// Runs <see cref="SqliteImporter"/> by itself, on the first start that finds a pre-ADR-0024 SQLite file
/// next to an empty PostgreSQL database — so upgrading is "add postgres, set the connection string,
/// start", with no command to run and no window in which the instance is up and looks empty.
/// </summary>
/// <remarks>
/// <para>
/// Three conditions, all of them required. The target must hold nothing but what the migrations seed
/// (<see cref="SqliteImporter.HoldsUserDataAsync"/> — the same question the explicit command refuses
/// on); the legacy file must be at the path the shipped image used, or wherever the removed
/// <c>Watchtower:DbPath</c> still points; and this database must not already carry the sentinel. The
/// file outlives the import on purpose (docs/upgrading.md keeps deleting it until the operator is
/// satisfied), so "the file is there" can never be the whole answer on its own.
/// </para>
/// <para>
/// <b>It runs before the host reads any configuration, not during startup.</b> Watchtower layers the
/// <c>elarion_settings</c> table into <see cref="IConfiguration"/> and snapshots it synchronously at
/// builder time, because pipeline decisions are taken from it — <c>Auth:Enabled</c> above all, which
/// decides whether authentication is wired into the pipeline at all. Importing after that snapshot would
/// leave the first post-upgrade process running on the defaults of an empty database while the imported
/// rows said otherwise: an installation with login enabled would come up with it off, until somebody
/// restarted it. So the import goes first and the snapshot reads the imported values.
/// </para>
/// <para>
/// The price of that window is that there is no service provider yet: the connection string comes
/// straight off <see cref="IConfiguration"/>, the logger is the host's console one built early, and the
/// sentinel is read and written as SQL against <c>elarion_settings</c> — the same bare-connection
/// treatment, and the same documented scope encoding, that <c>RuntimeSettingsLayering</c> already uses to
/// preload settings in this window.
/// </para>
/// <para>
/// <b>Nothing here is allowed to stop the host.</b> A corrupt or half-written legacy file is a bad reason
/// to leave an instance restarting forever, and the operator still has <c>--import-sqlite</c> for a
/// deliberate, diagnosable retry. On any failure the sentinel is not written, so the next start tries
/// again, and the message says what to do.
/// </para>
/// </remarks>
public static class SqliteAutoImport {
    /// <summary>Where the shipped image kept the SQLite database before ADR-0024.</summary>
    public const string DefaultDatabasePath = "/data/watchtower.db";

    /// <summary>
    /// The removed configuration path, still read for this one upgrade so a deployment that moved its
    /// database with <c>WATCHTOWER__DBPATH</c> is found where its file actually is rather than not at all.
    /// </summary>
    private const string DbPathSetting = "Watchtower:DbPath";

    /// <summary>
    /// The advisory lock two instances upgrading together serialize on. An arbitrary constant, chosen
    /// once and never reused; PostgreSQL's advisory locks share one namespace per database.
    /// </summary>
    /// <remarks>
    /// Session-scoped rather than <c>pg_advisory_xact_lock</c> on a caller's transaction, because the
    /// import itself truncates and rewrites nearly every table: a transaction that had already read the
    /// settings row would hold locks the importer's <c>TRUNCATE</c> waits on, and the two would deadlock.
    /// A dedicated connection holds it instead, and closing that connection releases it on every path out.
    /// </remarks>
    private const long ImportLockKey = 4919003;

    /// <summary>Prefix on every line of the importer's own summary, so it reads as one event in the log.</summary>
    private const string LinePrefix = "  sqlite-import| ";

    /// <summary>
    /// Imports the legacy database if all three conditions hold, and returns whether it did. Never
    /// throws.
    /// </summary>
    public static async Task<bool> RunAsync(
        IConfiguration configuration, ILogger logger, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        // Cheapest condition first, and the one that is false for every installation that never had a
        // SQLite database: no file, nothing to think about, no connection opened.
        var sqlitePath = ResolvePath(configuration);
        if (!File.Exists(sqlitePath)) return false;

        var connectionString = WatchtowerConnectionString.Find(configuration);
        // Not this code's error to report: the host fails on a missing connection string with a message
        // that explains the whole ADR-0024 change, and saying it twice would only bury it.
        if (string.IsNullOrWhiteSpace(connectionString)) return false;

        try {
            return await ImportIfUnclaimedAsync(connectionString, sqlitePath, logger, ct);
        } catch (Exception ex) {
            logger.LogError(
                ex, "Could not import the legacy database at {Path}; starting with the PostgreSQL "
                    + "database as it is. Nothing was written, and the next start will try again — or "
                    + "import it deliberately with `--import-sqlite {Path}`, which reports what went "
                    + "wrong in full.", sqlitePath, sqlitePath);
            return false;
        }
    }

    private static async Task<bool> ImportIfUnclaimedAsync(
        string connectionString, string sqlitePath, ILogger logger, CancellationToken ct) {
        // Held for the whole decision *and* the import, so a second instance starting at the same moment
        // waits and then sees the sentinel rather than a second empty database.
        await using var guard = new NpgsqlConnection(connectionString);
        await guard.OpenAsync(ct);
        await using (var lockCommand = guard.CreateCommand()) {
            lockCommand.CommandText = $"SELECT pg_advisory_lock({ImportLockKey})";
            await lockCommand.ExecuteNonQueryAsync(ct);
        }

        // The schema has to exist before either guard can be asked. Idempotent, and the host's own
        // MigrateAsync a moment later is then a no-op; doing it here rather than leaving it to the
        // importer keeps the two probes below reading a database that is fully migrated either way.
        var options = new DbContextOptionsBuilder<WatchtowerDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WatchtowerDbContext(options))
            await db.Database.MigrateAsync(ct);

        await using (var target = new NpgsqlConnection(connectionString)) {
            await target.OpenAsync(ct);
            if (await ReadSentinelAsync(target, ct) is not null) return false;

            if (await SqliteImporter.HoldsUserDataAsync(target, ct)) {
                // The ordinary state of an installation that has already been imported (by hand, before
                // this existed) and has not got round to deleting the file. Debug, not a warning: there
                // is nothing for anyone to do about it.
                logger.LogDebug(
                    "A legacy database is still present at {Path}, but this PostgreSQL database already "
                    + "holds data — not importing.", sqlitePath);
                return false;
            }
        }

        logger.LogInformation(
            "Empty PostgreSQL database and a legacy SQLite database at {Path}: importing it (ADR-0024). "
            + "The file is not deleted.", sqlitePath);

        var summary = new StringWriter();
        var exitCode = await SqliteImporter.RunAsync(connectionString, sqlitePath, summary, ct);
        if (exitCode != 0) {
            foreach (var line in Lines(summary)) logger.LogError("{Line}", LinePrefix + line);
            logger.LogError(
                "Importing {Path} failed; nothing was written and Watchtower starts with the empty "
                + "database. The next start will try again, or import it deliberately with "
                + "`--import-sqlite {Path}`.", sqlitePath, sqlitePath);
            return false;
        }

        foreach (var line in Lines(summary)) logger.LogInformation("{Line}", LinePrefix + line);

        await using (var writer = new NpgsqlConnection(connectionString)) {
            await writer.OpenAsync(ct);
            // After the import, because the import truncates and repopulates elarion_settings from the
            // source — a sentinel written before it would be thrown away by the very run it records.
            await WriteSentinelAsync(writer, ct);
        }

        await using (var db = new WatchtowerDbContext(options)) {
            db.AuditEvents.Add(new AuditEvent {
                Category = "system",
                Action = "db.import",
                Target = sqlitePath,
                // Actor-less: nobody asked for this, the upgrade did. The row is what makes the estate's
                // sudden appearance explicable to whoever looks at the trail afterwards.
                Detail = "imported the legacy SQLite database into PostgreSQL on first start (ADR-0024)",
                Success = true,
                CreatedAt = DateTimeOffset.UtcNow.ToMicrosecondPrecision(),
            });
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Imported {Path}. Check your stacks, routes and accounts, then delete the old file — nothing "
            + "reads it any more.", sqlitePath);
        return true;
    }

    /// <summary>
    /// Where the legacy file is: whatever the deployment still configures, otherwise the path the shipped
    /// image used. Straight off <see cref="IConfiguration"/> because the setting is gone from the model —
    /// this and <see cref="FileStateImport"/> are the only code left that knows it existed.
    /// </summary>
    private static string ResolvePath(IConfiguration configuration) {
        var configured = configuration[DbPathSetting];
        return string.IsNullOrWhiteSpace(configured) ? DefaultDatabasePath : configured.Trim();
    }

    // ── The sentinel, in the settings table, without a settings manager ──────────

    /// <summary>Scope encoding per Elarion.Settings: Global is kind "global" with an empty owner.</summary>
    private static async Task<string?> ReadSentinelAsync(NpgsqlConnection target, CancellationToken ct) {
        await using var command = target.CreateCommand();
        command.CommandText =
            """SELECT "value" FROM elarion_settings WHERE kind = 'global' AND owner = '' AND "key" = @key""";
        command.Parameters.AddWithValue("key", WatchtowerSettingPaths.DatabaseSqliteImported);
        var value = await command.ExecuteScalarAsync(ct);
        return value is null or DBNull ? null : (string)value;
    }

    private static async Task WriteSentinelAsync(NpgsqlConnection target, CancellationToken ct) {
        await using var command = target.CreateCommand();
        command.CommandText = """
            INSERT INTO elarion_settings (kind, owner, "key", "value", updated_on_utc, version)
            VALUES ('global', '', @key, @value, @now, 1)
            ON CONFLICT (kind, owner, "key") DO UPDATE
                SET "value" = EXCLUDED."value",
                    updated_on_utc = EXCLUDED.updated_on_utc,
                    version = elarion_settings.version + 1
            """;
        command.Parameters.AddWithValue("key", WatchtowerSettingPaths.DatabaseSqliteImported);
        command.Parameters.AddWithValue("value", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToMicrosecondPrecision());
        await command.ExecuteNonQueryAsync(ct);
    }

    private static IEnumerable<string> Lines(StringWriter summary) => summary
        .ToString()
        .Split('\n')
        .Select(line => line.TrimEnd('\r'))
        .Where(line => line.Length > 0);
}
