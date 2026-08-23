using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services.SqliteImport;

/// <summary>
/// The one-shot upgrade path from the pre-ADR-0024 SQLite file into PostgreSQL:
/// <c>dotnet Watchtower.Api.dll --import-sqlite /data/watchtower.db</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the only SQLite code left in Watchtower, and it is a command rather than a runtime mode.
/// It is deliberately schema-agnostic: the target's tables, columns, types and foreign keys are read
/// from <c>information_schema</c> after the PostgreSQL migrations have run, and each SQLite table is
/// copied column-by-name into its namesake. That means it keeps working as the model evolves — a
/// column added after this was written is simply absent from the source and takes its default, and a
/// table dropped since is skipped with a line in the summary. The alternative, a hand-written
/// per-entity copy, would have to be maintained forever for a command each installation runs once.
/// </para>
/// <para>
/// It refuses to run against a target that already holds data. Importing twice would either violate
/// primary keys or silently duplicate an estate, and neither failure is one an operator should have
/// to recognise at 2am — so the check happens before the first row is written.
/// </para>
/// </remarks>
public static class SqliteImporter {
    /// <summary>EF's own bookkeeping, which the migrations write and the import must never touch.</summary>
    private static readonly HashSet<string> ExcludedTables =
        new(StringComparer.OrdinalIgnoreCase) { "__EFMigrationsHistory", "__ef_migrations_history" };

    /// <summary>
    /// Rows the migrations themselves write, which therefore do not mean "this database is in use".
    /// Today: the seeded operator realm.
    /// </summary>
    private static readonly HashSet<string> SeededTables =
        new(StringComparer.Ordinal) { "realms" };

    /// <summary>
    /// Migrates the configured PostgreSQL database, then copies every table of
    /// <paramref name="sqlitePath"/> into it. Returns a process exit code: 0 on success, 1 on any
    /// refusal or failure.
    /// </summary>
    public static async Task<int> RunAsync(
        string connectionString,
        string sqlitePath,
        TextWriter output,
        CancellationToken ct = default) {
        if (!File.Exists(sqlitePath)) {
            await output.WriteLineAsync($"error: SQLite database not found: {sqlitePath}");
            return 1;
        }

        try {
            await ImportAsync(connectionString, sqlitePath, output, ct);
            return 0;
        } catch (ImportRefusedException refused) {
            await output.WriteLineAsync($"error: {refused.Message}");
            return 1;
        } catch (Exception ex) {
            await output.WriteLineAsync($"error: import failed, nothing was written: {ex.Message}");
            return 1;
        }
    }

    private static async Task ImportAsync(
        string connectionString, string sqlitePath, TextWriter output, CancellationToken ct) {
        await output.WriteLineAsync($"Importing {sqlitePath} into PostgreSQL.");

        // The target schema comes from the normal migrations, so an import lands on exactly the schema a
        // fresh install would have — there is no import-specific DDL to keep in step with the model.
        var contextOptions = new DbContextOptionsBuilder<WatchtowerDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WatchtowerDbContext(contextOptions))
            await db.Database.MigrateAsync(ct);
        await output.WriteLineAsync("Target migrated.");

        await using var target = new NpgsqlConnection(connectionString);
        await target.OpenAsync(ct);

        var schema = await ReadTargetSchemaAsync(target, ct);
        var order = OrderByDependencies(schema);

        var occupied = await FindOccupiedAsync(target, schema, ct);
        if (occupied.Count > 0)
            throw new ImportRefusedException(
                $"the target database already holds data ({string.Join(", ", occupied)}). "
                + "Import into an empty database — drop and recreate it, or point at a new one.");

        await using var sqlite = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = sqlitePath, Mode = SqliteOpenMode.ReadOnly }
                .ToString());
        await sqlite.OpenAsync(ct);
        var sourceTables = await ReadSqliteTableNamesAsync(sqlite, ct);

        // One transaction for the whole import: a half-copied estate is worse than no copy at all,
        // because the emptiness guard above would then refuse the retry.
        await using var transaction = await target.BeginTransactionAsync(ct);

        // The table graph has a genuine cycle — a realm names its login route, and that route names the
        // realm it serves — so no ordering of whole-row copies can satisfy both foreign keys as it goes.
        // Deferring every foreign key to commit time is what makes the copy possible at all; the ordering
        // below is still worth doing, because it keeps the deferred check list short. Restored before
        // commit, so the schema an operator ends up with is the one the migration built.
        await DeferForeignKeysAsync(target, deferred: true, ct);

        // The migrations seed rows of their own — the operator realm, today — and the source has its own
        // copy of every one of them, with the ids the rest of the source points at. Clearing the tables
        // the source is about to replace is what stops the two colliding. Only those tables: a table the
        // model seeds and the source does not have keeps its seed.
        var replaced = order.Where(sourceTables.Contains).ToList();
        if (replaced.Count > 0) {
            await using var truncate = target.CreateCommand();
            truncate.CommandText =
                $"TRUNCATE TABLE {string.Join(", ", replaced.Select(t => $"\"{t}\""))} "
                + "RESTART IDENTITY CASCADE";
            await truncate.ExecuteNonQueryAsync(ct);
        }

        var counts = new List<(string Table, long Rows)>();
        var skipped = new List<string>();
        foreach (var table in order) {
            if (!sourceTables.Contains(table)) {
                skipped.Add(table);
                continue;
            }
            var rows = await CopyTableAsync(sqlite, target, schema[table], output, ct);
            counts.Add((table, rows));
        }

        foreach (var orphan in sourceTables.Where(t => !schema.ContainsKey(t) && !ExcludedTables.Contains(t)))
            await output.WriteLineAsync($"  (source table '{orphan}' no longer exists in the model — skipped)");

        await ResetSequencesAsync(target, schema, ct);
        // After the sequences, because it inserts routes and must not reuse an imported id.
        var convertedHosts = await ConvertLegacyLoginHostsAsync(sqlite, target, schema, sourceTables, output, ct);
        // Force the deferred checks now rather than at COMMIT, so a broken source fails here — inside the
        // try that reports it — instead of throwing out of CommitAsync.
        await ExecuteAsync(target, "SET CONSTRAINTS ALL IMMEDIATE", ct);
        await DeferForeignKeysAsync(target, deferred: false, ct);
        await transaction.CommitAsync(ct);

        await output.WriteLineAsync();
        await output.WriteLineAsync("Imported:");
        foreach (var (table, rows) in counts.Where(c => c.Rows > 0).OrderBy(c => c.Table, StringComparer.Ordinal))
            await output.WriteLineAsync($"  {table,-40} {rows,8}");
        var emptyTables = counts.Count(c => c.Rows == 0);
        await output.WriteLineAsync(
            $"  ({emptyTables} table(s) empty, {skipped.Count} table(s) absent from the source)");
        if (convertedHosts > 0)
            await output.WriteLineAsync($"  converted {convertedHosts} legacy realm login host(s).");
        await output.WriteLineAsync($"Total: {counts.Sum(c => c.Rows)} row(s) across {counts.Count} table(s).");
    }

    // ── The one legacy column that is converted rather than dropped ──────────────

    /// <summary>
    /// Replays ADR-0023's conversion for a source that predates it: a realm's <c>auth_host</c> becomes a
    /// <see cref="Entities.RouteTarget.Watchtower"/> route, and that route becomes the realm's login
    /// route. Returns how many realms were converted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rest of this importer is schema-agnostic on purpose, and this is the one place that knows
    /// something about the model. It earns the exception because <c>auth_host</c> is the only dropped
    /// column whose value is <em>load-bearing</em>: it is where a realm's login page lived, and a realm
    /// that loses it can no longer admit anyone. The conversion used to be a migration
    /// (<c>ConvertLoginHostsToRoutes</c>), which ADR-0024 regenerated away — so for a database that never
    /// ran that migration, this is the only thing left that can do it.
    /// </para>
    /// <para>
    /// It declines rather than guesses in the two ambiguous cases: a hostname already served by a
    /// <em>service</em> route is left alone (re-pointing an application's domain at the management plane
    /// is the worst possible reading of an upgrade), and so is one already claimed by another realm's
    /// Watchtower route. Both are reported; the operator picks a hostname. This mirrors
    /// <see cref="LoginHostConversion"/>, which does the same job for the configured <c>Auth:Host</c> on
    /// the first start.
    /// </para>
    /// </remarks>
    private static async Task<int> ConvertLegacyLoginHostsAsync(
        SqliteConnection sqlite,
        NpgsqlConnection target,
        Dictionary<string, TargetTable> schema,
        HashSet<string> sourceTables,
        TextWriter output,
        CancellationToken ct) {
        if (!sourceTables.Contains("realms")) return 0;
        if (!schema.TryGetValue("realms", out var realms) || !schema.TryGetValue("routes", out var routes))
            return 0;
        // The model has to still be shaped the way this conversion targets. If a later ADR moves either
        // column, doing nothing is right — the value is then somebody else's to carry.
        if (!realms.Columns.Any(c => c.Name == "login_route_id")) return 0;
        if (!routes.Columns.Any(c => c.Name == "target") || !routes.Columns.Any(c => c.Name == "realm_id"))
            return 0;

        var sourceColumns = await ReadSqliteColumnNamesAsync(sqlite, "realms", ct);
        if (!sourceColumns.Contains("auth_host")) return 0;

        var legacy = new List<(long RealmId, string Domain)>();
        await using (var select = sqlite.CreateCommand()) {
            select.CommandText =
                """SELECT "id", "auth_host" FROM "realms" WHERE "auth_host" IS NOT NULL ORDER BY "id" """;
            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                if (reader.IsDBNull(1)) continue;
                var domain = NormalizeDomain(reader.GetString(1));
                if (domain.Length == 0) continue;
                legacy.Add((reader.GetInt64(0), domain));
            }
        }
        if (legacy.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow.UtcDateTime;
        var converted = 0;
        foreach (var (realmId, domain) in legacy) {
            var existing = await FindRouteAsync(target, domain, ct);
            int routeId;

            if (existing is null) {
                await using var insert = target.CreateCommand();
                insert.CommandText = """
                    INSERT INTO "routes" ("target", "realm_id", "stack_id", "domain", "service_name",
                                          "container_port", "tls_enabled", "is_primary", "kind",
                                          "access_mode", "identity_header_mode", "status", "created_at")
                    VALUES ('Watchtower', @realm, NULL, @domain, '', 0, true, false, 'Managed',
                            'Public', 'None', 'Pending', @now)
                    RETURNING "id"
                    """;
                insert.Parameters.AddWithValue("realm", (int)realmId);
                insert.Parameters.AddWithValue("domain", domain);
                insert.Parameters.AddWithValue("now", now);
                routeId = Convert.ToInt32(await insert.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            } else if (existing.Value.Target != "Watchtower") {
                await output.WriteLineAsync(
                    $"  warning: realm {realmId}'s login host '{domain}' is already a service route — "
                    + "left alone. Give the realm a Watchtower route on another hostname.");
                continue;
            } else if (existing.Value.RealmId != realmId) {
                await output.WriteLineAsync(
                    $"  warning: realm {realmId}'s login host '{domain}' already serves Watchtower for "
                    + "another realm — left alone. Give the realm a hostname of its own.");
                continue;
            } else {
                routeId = existing.Value.Id;
            }

            await using var link = target.CreateCommand();
            link.CommandText =
                """UPDATE "realms" SET "login_route_id" = @route WHERE "id" = @realm AND "login_route_id" IS NULL""";
            link.Parameters.AddWithValue("route", routeId);
            link.Parameters.AddWithValue("realm", (int)realmId);
            await link.ExecuteNonQueryAsync(ct);
            converted++;
        }
        return converted;
    }

    private static async Task<(int Id, string Target, long RealmId)?> FindRouteAsync(
        NpgsqlConnection target, string domain, CancellationToken ct) {
        await using var command = target.CreateCommand();
        command.CommandText = """SELECT "id", "target", "realm_id" FROM "routes" WHERE "domain" = @domain""";
        command.Parameters.AddWithValue("domain", domain);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? -1 : reader.GetInt32(2));
    }

    /// <summary>
    /// Every table that holds a row the migrations did not put there. Named tables rather than a
    /// hand-picked handful, because "is this database in use?" is not a question a fixed list of four
    /// can answer — an instance whose only content is a credential or a backup schedule is still one an
    /// import would trample.
    /// </summary>
    private static async Task<List<string>> FindOccupiedAsync(
        NpgsqlConnection target, Dictionary<string, TargetTable> schema, CancellationToken ct) {
        var occupied = new List<string>();
        foreach (var table in schema.Keys.OrderBy(n => n, StringComparer.Ordinal)) {
            await using var command = target.CreateCommand();
            // The seeded tables are compared against what a fresh migration leaves behind, so a
            // never-used database reads as empty; anything above that line is real content.
            command.CommandText = $"""SELECT count(*) FROM "{table}" """;
            var count = Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
            if (count > (SeededTables.Contains(table) ? 1 : 0)) occupied.Add($"{table}: {count} row(s)");
        }
        return occupied;
    }

    /// <summary>
    /// Switches every foreign key in the target between deferrable-initially-deferred and its normal
    /// immediate form, and (when deferring) tells the open transaction to use it.
    /// </summary>
    /// <remarks>
    /// DDL is transactional in PostgreSQL, so both halves roll back with everything else if the import
    /// fails — a failed run leaves no deferrable constraints behind.
    /// </remarks>
    private static async Task DeferForeignKeysAsync(
        NpgsqlConnection target, bool deferred, CancellationToken ct) {
        var statements = new List<string>();
        await using (var command = target.CreateCommand()) {
            command.CommandText = $"""
                SELECT format('ALTER TABLE %I.%I ALTER CONSTRAINT %I {(deferred
                    ? "DEFERRABLE INITIALLY DEFERRED"
                    : "NOT DEFERRABLE")}',
                              n.nspname, t.relname, c.conname)
                FROM pg_constraint c
                JOIN pg_class t ON t.oid = c.conrelid
                JOIN pg_namespace n ON n.oid = t.relnamespace
                WHERE c.contype = 'f' AND n.nspname = 'public'
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) statements.Add(reader.GetString(0));
        }
        foreach (var statement in statements) await ExecuteAsync(target, statement, ct);
        if (deferred) await ExecuteAsync(target, "SET CONSTRAINTS ALL DEFERRED", ct);
    }

    private static async Task ExecuteAsync(NpgsqlConnection target, string sql, CancellationToken ct) {
        await using var command = target.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    // ── Target schema ────────────────────────────────────────────────────────────

    private sealed record TargetColumn(string Name, string DataType, bool IsIdentity);

    private sealed record TargetTable(string Name, List<TargetColumn> Columns, HashSet<string> DependsOn);

    private static async Task<Dictionary<string, TargetTable>> ReadTargetSchemaAsync(
        NpgsqlConnection target, CancellationToken ct) {
        var tables = new Dictionary<string, TargetTable>(StringComparer.Ordinal);

        await using (var command = target.CreateCommand()) {
            command.CommandText = """
                -- COALESCE because column_default is NULL for most columns, and `false OR NULL` is NULL.
                SELECT c.table_name, c.column_name, c.data_type,
                       COALESCE(c.is_identity = 'YES', false)
                           OR COALESCE(c.column_default LIKE 'nextval(%', false) AS is_identity
                FROM information_schema.columns c
                JOIN information_schema.tables t
                  ON t.table_schema = c.table_schema AND t.table_name = c.table_name
                WHERE c.table_schema = 'public' AND t.table_type = 'BASE TABLE'
                ORDER BY c.table_name, c.ordinal_position
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                var tableName = reader.GetString(0);
                if (ExcludedTables.Contains(tableName)) continue;
                if (!tables.TryGetValue(tableName, out var table))
                    tables[tableName] = table = new TargetTable(tableName, [], new HashSet<string>(StringComparer.Ordinal));
                table.Columns.Add(new TargetColumn(reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)));
            }
        }

        await using (var command = target.CreateCommand()) {
            command.CommandText = """
                SELECT tc.table_name AS child, ccu.table_name AS parent
                FROM information_schema.table_constraints tc
                JOIN information_schema.constraint_column_usage ccu
                  ON ccu.constraint_schema = tc.constraint_schema
                 AND ccu.constraint_name = tc.constraint_name
                WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_schema = 'public'
                """;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) {
                var child = reader.GetString(0);
                var parent = reader.GetString(1);
                // A self-reference (a route's parent route, a group's parent group) is not an ordering
                // constraint between tables; it is one within a table, and PostgreSQL only checks it at
                // statement end, so a single COPY satisfies it.
                if (child == parent) continue;
                if (tables.TryGetValue(child, out var table) && tables.ContainsKey(parent))
                    table.DependsOn.Add(parent);
            }
        }

        return tables;
    }

    /// <summary>
    /// Parents before children, so every foreign key is satisfiable row-by-row. A cycle (none exists
    /// today) is broken by emitting the remaining tables in name order rather than failing — the copy
    /// would then rely on PostgreSQL's per-statement constraint check, which is exactly what happens
    /// for self-references anyway.
    /// </summary>
    private static List<string> OrderByDependencies(Dictionary<string, TargetTable> tables) {
        var ordered = new List<string>(tables.Count);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var remaining = tables.Keys.OrderBy(n => n, StringComparer.Ordinal).ToList();

        while (remaining.Count > 0) {
            var ready = remaining.Where(n => tables[n].DependsOn.All(placed.Contains)).ToList();
            if (ready.Count == 0) ready = remaining.ToList();
            foreach (var name in ready) {
                ordered.Add(name);
                placed.Add(name);
            }
            remaining.RemoveAll(placed.Contains);
        }
        return ordered;
    }

    // ── Source ───────────────────────────────────────────────────────────────────

    private static async Task<HashSet<string>> ReadSqliteTableNamesAsync(
        SqliteConnection sqlite, CancellationToken ct) {
        await using var command = sqlite.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var names = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<HashSet<string>> ReadSqliteColumnNamesAsync(
        SqliteConnection sqlite, string table, CancellationToken ct) {
        await using var command = sqlite.CreateCommand();
        // The table name is a target-schema name, never operator input.
        command.CommandText = $"""SELECT name FROM pragma_table_info('{table}')""";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<long> CopyTableAsync(
        SqliteConnection sqlite,
        NpgsqlConnection target,
        TargetTable table,
        TextWriter output,
        CancellationToken ct) {
        await using var select = sqlite.CreateCommand();
        select.CommandText = $"""SELECT * FROM "{table.Name}" """;
        await using var reader = await select.ExecuteReaderAsync(ct);

        // Column-by-name: the source's column order is the old migration history's, not the model's.
        var sourceOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++) sourceOrdinals[reader.GetName(i)] = i;

        var mapped = table.Columns
            .Where(c => sourceOrdinals.ContainsKey(c.Name))
            .Select(c => (Column: c, Ordinal: sourceOrdinals[c.Name]))
            .ToList();
        if (mapped.Count == 0) return 0;

        foreach (var missing in table.Columns.Where(c => !sourceOrdinals.ContainsKey(c.Name)))
            await output.WriteLineAsync(
                $"  note: {table.Name}.{missing.Name} is absent from the source — taking its default.");

        // The other direction, and the one that loses information: a column the source has and the model
        // does not. Silence here would mean an operator only discovers the loss when they look for the
        // value. Warned rather than refused, because a dropped column is usually deliberate — the one
        // case that is *not* just dropped, realms.auth_host, is converted after the copy
        // (ConvertLegacyLoginHostsAsync).
        var targetColumns = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dropped in sourceOrdinals.Keys.Where(n => !targetColumns.Contains(n)).Order(StringComparer.Ordinal))
            await output.WriteLineAsync(
                $"  warning: {table.Name}.{dropped} exists in the source but not in the model — not imported.");

        var columnList = string.Join(", ", mapped.Select(m => $"\"{m.Column.Name}\""));
        await using var writer = await target.BeginBinaryImportAsync(
            $"""COPY "{table.Name}" ({columnList}) FROM STDIN (FORMAT BINARY)""", ct);

        long rows = 0;
        while (await reader.ReadAsync(ct)) {
            await writer.StartRowAsync(ct);
            foreach (var (column, ordinal) in mapped)
                await WriteValueAsync(writer, column, reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal), table.Name, ct);
            rows++;
        }
        await writer.CompleteAsync(ct);
        return rows;
    }

    /// <summary>
    /// Converts one SQLite value to the target column's type. SQLite has no types to speak of — the
    /// PostgreSQL column is the authority, and every conversion here is driven by it.
    /// </summary>
    private static async Task WriteValueAsync(
        NpgsqlBinaryImporter writer, TargetColumn column, object? value, string table, CancellationToken ct) {
        if (value is null) {
            await writer.WriteNullAsync(ct);
            return;
        }

        switch (column.DataType) {
            case "boolean":
                await writer.WriteAsync(ToBoolean(value), NpgsqlDbType.Boolean, ct);
                break;
            case "smallint":
                await writer.WriteAsync(Convert.ToInt16(value, CultureInfo.InvariantCulture), NpgsqlDbType.Smallint, ct);
                break;
            case "integer":
                await writer.WriteAsync(Convert.ToInt32(value, CultureInfo.InvariantCulture), NpgsqlDbType.Integer, ct);
                break;
            case "bigint":
                await writer.WriteAsync(Convert.ToInt64(value, CultureInfo.InvariantCulture), NpgsqlDbType.Bigint, ct);
                break;
            case "real":
                await writer.WriteAsync(Convert.ToSingle(value, CultureInfo.InvariantCulture), NpgsqlDbType.Real, ct);
                break;
            case "double precision":
                await writer.WriteAsync(Convert.ToDouble(value, CultureInfo.InvariantCulture), NpgsqlDbType.Double, ct);
                break;
            case "numeric":
                await writer.WriteAsync(Convert.ToDecimal(value, CultureInfo.InvariantCulture), NpgsqlDbType.Numeric, ct);
                break;
            case "bytea":
                await writer.WriteAsync(ToBytes(value), NpgsqlDbType.Bytea, ct);
                break;
            case "uuid":
                await writer.WriteAsync(Guid.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!), NpgsqlDbType.Uuid, ct);
                break;
            case "timestamp with time zone":
                // Npgsql accepts a timestamptz only as UTC; EF/SQLite wrote the offset into the text, so
                // an installation that ever ran with a non-UTC offset is normalized here rather than
                // rejected. (Watchtower writes DateTimeOffset.UtcNow everywhere, so this is belt-and-braces.)
                await writer.WriteAsync(ToDateTimeOffset(value, column, table).UtcDateTime, NpgsqlDbType.TimestampTz, ct);
                break;
            case "timestamp without time zone":
                await writer.WriteAsync(ToDateTimeOffset(value, column, table).UtcDateTime, NpgsqlDbType.Timestamp, ct);
                break;
            default:
                // text, character varying, and anything else that round-trips through its string form.
                await writer.WriteAsync(NormalizeText(value, column, table), ct);
                break;
        }
    }

    /// <summary>
    /// A text value on its way in. Only <c>routes.domain</c> is touched, and only to normalize it.
    /// </summary>
    /// <remarks>
    /// Defence in depth. Every write path already normalizes the column (<c>DesiredHosts.TryNormalize</c>),
    /// and the host lookups now depend on that — they compare a normalized parameter against the raw
    /// column so the index is usable. A source row that predates that guarantee, or was edited by hand,
    /// would otherwise become a route the proxy can never match: present in the table, invisible to
    /// every request.
    /// </remarks>
    private static string NormalizeText(object value, TargetColumn column, string table) {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture)!;
        return table == "routes" && column.Name == "domain" ? NormalizeDomain(text) : text;
    }

    /// <summary>Trimmed, without a trailing root dot, lowercase — the form every route write stores.</summary>
    private static string NormalizeDomain(string domain) {
        var trimmed = domain.Trim();
        if (trimmed.EndsWith('.')) trimmed = trimmed[..^1];
        return trimmed.ToLowerInvariant();
    }

    private static bool ToBoolean(object value) => value switch {
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        string s => s is "1" or "true" or "True" or "TRUE",
        _ => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
    };

    private static byte[] ToBytes(object value) => value switch {
        byte[] bytes => bytes,
        string s => Convert.FromBase64String(s),
        _ => throw new InvalidOperationException($"cannot read a blob from {value.GetType().Name}"),
    };

    private static DateTimeOffset ToDateTimeOffset(object value, TargetColumn column, string table) {
        switch (value) {
            case DateTimeOffset dto:
                return dto;
            case DateTime dt:
                return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            case string text when DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed):
                return parsed;
            case long unixSeconds:
                return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
            default:
                throw new InvalidOperationException(
                    $"cannot read a timestamp from {table}.{column.Name} (value '{value}')");
        }
    }

    // ── Sequences ────────────────────────────────────────────────────────────────

    /// <summary>
    /// COPY bypasses the identity sequences, so every one of them still points at 1 afterwards and the
    /// first insert after the import would collide with an imported row. This walks them forward.
    /// </summary>
    private static async Task ResetSequencesAsync(
        NpgsqlConnection target, Dictionary<string, TargetTable> schema, CancellationToken ct) {
        foreach (var table in schema.Values)
        foreach (var column in table.Columns.Where(c => c.IsIdentity)) {
            await using var command = target.CreateCommand();
            command.CommandText = $"""
                SELECT setval(
                    pg_get_serial_sequence('public."{table.Name}"', '{column.Name}'),
                    COALESCE((SELECT max("{column.Name}") FROM "{table.Name}"), 1),
                    COALESCE((SELECT max("{column.Name}") FROM "{table.Name}"), 0) > 0)
                WHERE pg_get_serial_sequence('public."{table.Name}"', '{column.Name}') IS NOT NULL
                """;
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private sealed class ImportRefusedException(string message) : Exception(message);
}
