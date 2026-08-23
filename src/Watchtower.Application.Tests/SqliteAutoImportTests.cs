using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.SqliteImport;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the automatic half of the ADR-0024 upgrade: the first start that finds a pre-ADR-0024 SQLite
/// file beside an empty PostgreSQL database imports it, with no command for the operator to run.
/// </summary>
/// <remarks>
/// The source files come from <see cref="SqliteImporterTests.WriteLegacyDatabase"/> — the same hand-built
/// fixture the explicit command is tested against, because the two paths run the same importer and
/// what is under test here is only the decision to run it.
/// </remarks>
public sealed class SqliteAutoImportTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ImportsOnTheFirstStart_WhenALegacyFileSitsBesideAnEmptyDatabase() {
        var sqlitePath = SqliteImporterTests.WriteLegacyDatabase();
        var connectionString = PostgresTestServer.CreateDatabase();
        try {
            var imported = await SqliteAutoImport.RunAsync(Configuration(connectionString, sqlitePath), NullLogger.Instance, Ct);

            Assert.True(imported);
            await using var db = Context(connectionString);
            Assert.Equal("demo", (await db.Stacks.AsNoTracking().SingleAsync(Ct)).Name);
            Assert.NotNull(await ReadSentinelAsync(connectionString));

            // The file is left exactly where it was: the rollback path in docs/upgrading.md depends on it.
            Assert.True(File.Exists(sqlitePath));
        } finally {
            PostgresTestServer.Drop(connectionString);
            File.Delete(sqlitePath);
        }
    }

    /// <summary>
    /// The condition that keeps this from being dangerous. A database in use is an installation, and an
    /// import would truncate every table it has — so the presence of an old file decides nothing on its
    /// own, and an operator who has not deleted theirs yet is not punished for it.
    /// </summary>
    [Fact]
    public async Task DoesNotImport_WhenThePostgresDatabaseAlreadyHoldsData() {
        var sqlitePath = SqliteImporterTests.WriteLegacyDatabase();
        var connectionString = PostgresTestServer.CreateDatabase();
        try {
            await using (var seed = Context(connectionString)) {
                seed.Stacks.Add(new Stack {
                    Name = "in-use",
                    RepositoryUrl = "https://example.invalid/in-use.git",
                    ComposeFilePath = "docker-compose.yml",
                    Branch = "main",
                    ComposeProjectName = "in-use",
                });
                await seed.SaveChangesAsync(Ct);
            }

            var imported = await SqliteAutoImport.RunAsync(Configuration(connectionString, sqlitePath), NullLogger.Instance, Ct);

            Assert.False(imported);
            await using var db = Context(connectionString);
            Assert.Equal("in-use", (await db.Stacks.AsNoTracking().SingleAsync(Ct)).Name);
            // Not written on a path that declined: the decision was about today's rows, not this database.
            Assert.Null(await ReadSentinelAsync(connectionString));
        } finally {
            PostgresTestServer.Drop(connectionString);
            File.Delete(sqlitePath);
        }
    }

    /// <summary>
    /// The sentinel is the answer to the case emptiness cannot cover: an estate deleted down to nothing
    /// after the upgrade would otherwise be replaced by the old file on the next restart.
    /// </summary>
    [Fact]
    public async Task DoesNotImport_WhenThisDatabaseHasAlreadyBeenDecided() {
        var sqlitePath = SqliteImporterTests.WriteLegacyDatabase();
        var connectionString = PostgresTestServer.CreateDatabase();
        try {
            // Only the sentinel, on an otherwise untouched database — so nothing but the sentinel can be
            // what declines. That is the state an emptied estate leaves behind, and the case emptiness
            // alone cannot answer.
            await WriteSentinelAsync(connectionString);

            var imported = await SqliteAutoImport.RunAsync(
                Configuration(connectionString, sqlitePath), NullLogger.Instance, Ct);

            Assert.False(imported);
            await using var db = Context(connectionString);
            Assert.Empty(await db.Stacks.AsNoTracking().ToListAsync(Ct));
        } finally {
            PostgresTestServer.Drop(connectionString);
            File.Delete(sqlitePath);
        }
    }

    /// <summary>
    /// A file that cannot be read must cost the operator a log line, not their instance: an unreadable
    /// upgrade artefact that crashed the host would restart-loop a deployment over data nobody needs to
    /// serve a request.
    /// </summary>
    [Fact]
    public async Task AnUnreadableFile_LeavesNoSentinel_AndLetsStartupContinue() {
        var sqlitePath = Path.Combine(Path.GetTempPath(), $"watchtower-corrupt-{Guid.NewGuid():N}.db");
        await File.WriteAllTextAsync(sqlitePath, "this is not a SQLite database", Ct);
        var connectionString = PostgresTestServer.CreateDatabase();
        try {
            var imported = await SqliteAutoImport.RunAsync(
                Configuration(connectionString, sqlitePath), NullLogger.Instance, Ct);

            Assert.False(imported);
            // No sentinel, so a repaired or replaced file is still picked up on the next start.
            Assert.Null(await ReadSentinelAsync(connectionString));
            await using var db = Context(connectionString);
            Assert.Empty(await db.Stacks.AsNoTracking().ToListAsync(Ct));
        } finally {
            PostgresTestServer.Drop(connectionString);
            File.Delete(sqlitePath);
        }
    }

    /// <summary>The overwhelmingly common case: no legacy file, so not even a connection is opened.</summary>
    [Fact]
    public async Task DoesNothing_AndOpensNoConnection_WhenThereIsNoLegacyFile() {
        var absent = Path.Combine(Path.GetTempPath(), $"watchtower-absent-{Guid.NewGuid():N}.db");

        var imported = await SqliteAutoImport.RunAsync(
            Configuration("Host=127.0.0.1;Port=1;Database=unreachable;Username=x;Password=x", absent),
            NullLogger.Instance,
            Ct);

        Assert.False(imported);
    }

    private static IConfiguration Configuration(string connectionString, string sqlitePath) =>
        new ConfigurationBuilder().AddInMemoryCollection([
            new KeyValuePair<string, string?>(WatchtowerConnectionString.ConfigurationKey, connectionString),
            // The removed setting, still honoured for one release so a moved volume is still found.
            new KeyValuePair<string, string?>("Watchtower:DbPath", sqlitePath),
        ]).Build();

    private static WatchtowerDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<WatchtowerDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    /// <summary>
    /// Read as SQL, not through the settings manager: the sentinel is internal bookkeeping with no UI
    /// path, and the test should be able to tell an absent row from an empty value.
    /// </summary>
    private static async Task<string?> ReadSentinelAsync(string connectionString) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """SELECT "value" FROM elarion_settings WHERE kind = 'global' AND owner = '' AND "key" = @key""";
        command.Parameters.AddWithValue("key", WatchtowerSettingPaths.DatabaseSqliteImported);
        var value = await command.ExecuteScalarAsync(Ct);
        return value is null or DBNull ? null : (string)value;
    }

    private static async Task WriteSentinelAsync(string connectionString) {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO elarion_settings (kind, owner, "key", "value", updated_on_utc, version)
            VALUES ('global', '', @key, @value, now(), 1)
            """;
        command.Parameters.AddWithValue("key", WatchtowerSettingPaths.DatabaseSqliteImported);
        command.Parameters.AddWithValue("value", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(Ct);
    }
}
