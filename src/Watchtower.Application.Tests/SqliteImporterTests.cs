using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.SqliteImport;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the one-shot upgrade path from a pre-ADR-0024 SQLite file into PostgreSQL
/// (<c>--import-sqlite</c>): the round trip itself, the type conversions SQLite's lack of types forces,
/// the identity sequences COPY does not advance, and the refusal to run twice.
/// </summary>
/// <remarks>
/// The source is built here rather than checked in as a binary fixture: the old SQLite migrations are
/// gone with ADR-0024, so a real pre-upgrade file cannot be regenerated, and a committed one would be a
/// blob nobody could review. What matters for the importer is the <em>shape</em> of what EF's SQLite
/// provider wrote — integers for booleans, text for timestamps, column order that is the old migration
/// history's rather than the model's — so that is what these fixtures reproduce, by hand, in SQL.
/// </remarks>
public sealed class SqliteImporterTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The text shape EF's SQLite provider used for a <c>DateTimeOffset</c>.</summary>
    private const string LegacyTimestamp = "2026-01-02 03:04:05.6789000+00:00";

    private static readonly DateTimeOffset ExpectedTimestamp =
        new DateTimeOffset(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero).AddTicks(9000);

    [Fact]
    public async Task ImportsEveryTable_ConvertingValuesToTheTargetsTypes() {
        var sqlitePath = WriteLegacyDatabase();
        var connectionString = PostgresTestServer.CreateDatabase();
        var output = new StringWriter();
        try {
            var exitCode = await SqliteImporter.RunAsync(connectionString, sqlitePath, output, Ct);
            Assert.True(exitCode == 0, output.ToString());

            await using var db = Context(connectionString);

            // Realms: the seeded operator realm was replaced by the source's own copy, not duplicated.
            var realms = await db.Realms.AsNoTracking().OrderBy(r => r.Id).ToListAsync(Ct);
            Assert.Equal([1, 2], realms.Select(r => r.Id));
            Assert.True(realms[0].IsSystem);
            Assert.Equal("operator", realms[0].Slug);
            Assert.False(realms[1].IsSystem);
            Assert.Equal("acme", realms[1].Slug);

            // Stacks: booleans came from 0/1 integers, the timestamp from EF's SQLite text shape, and
            // the enum-as-string columns straight across.
            var stack = await db.Stacks.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(7, stack.Id);
            Assert.Equal("demo", stack.Name);
            Assert.True(stack.WebhookEnabled);
            Assert.False(stack.BackupEnabled);
            Assert.Equal(AutoDeployMode.OnChange, stack.AutoDeployMode);
            Assert.Equal(ExpectedTimestamp, stack.CreatedAt);
            Assert.Equal(TimeSpan.Zero, stack.CreatedAt.Offset);

            // Routes: the foreign keys survived, which is the whole point of the dependency ordering.
            var route = await db.Routes.AsNoTracking().SingleAsync(Ct);
            Assert.Equal(stack.Id, route.StackId);
            Assert.Equal("app.example.invalid", route.Domain);
            Assert.Equal(AccessMode.Authenticated, route.AccessMode);
            Assert.True(route.TlsEnabled);

            // A column the source predates takes the target's default rather than failing the copy.
            Assert.Equal(IdentityHeaderMode.None, route.IdentityHeaderMode);

            var user = await db.Users.AsNoTracking().SingleAsync(Ct);
            Assert.Equal("legacy", user.UserName);
            Assert.Equal(2, user.RealmId);
            Assert.True(user.IsAdmin);
            Assert.Null(user.LockoutEnd);

            Assert.Contains("stacks", output.ToString());
        } finally {
            PostgresTestServer.Drop(connectionString);
            File.Delete(sqlitePath);
        }
    }

    /// <summary>
    /// COPY writes explicit ids without touching the identity sequence, so without the <c>setval</c>
    /// pass the first row the application inserts after an import collides with an imported one.
    /// </summary>
    [Fact]
    public async Task AdvancesTheIdentitySequencesPastTheImportedRows() {
        var sqlitePath = WriteLegacyDatabase();
        var connectionString = PostgresTestServer.CreateDatabase();
        try {
            var first = new StringWriter();
            Assert.True(await SqliteImporter.RunAsync(connectionString, sqlitePath, first, Ct) == 0, first.ToString());

            await using var db = Context(connectionString);
            db.Stacks.Add(new Stack {
                Name = "next",
                RepositoryUrl = "https://example.invalid/next.git",
                ComposeFilePath = "docker-compose.yml",
                Branch = "main",
                ComposeProjectName = "next",
            });
            await db.SaveChangesAsync(Ct);

            var added = await db.Stacks.AsNoTracking().SingleAsync(s => s.Name == "next", Ct);
            Assert.True(added.Id > 7, $"expected an id above the imported 7, got {added.Id}");
        } finally {
            PostgresTestServer.Drop(connectionString);
            File.Delete(sqlitePath);
        }
    }

    /// <summary>
    /// Importing twice would either violate a primary key halfway through or duplicate an estate. The
    /// guard runs before the first row is written, so a mistaken second run changes nothing.
    /// </summary>
    [Fact]
    public async Task RefusesATargetThatAlreadyHoldsData() {
        var sqlitePath = WriteLegacyDatabase();
        var connectionString = PostgresTestServer.CreateDatabase();
        var output = new StringWriter();
        try {
            var first = new StringWriter();
            Assert.True(await SqliteImporter.RunAsync(connectionString, sqlitePath, first, Ct) == 0, first.ToString());

            var exitCode = await SqliteImporter.RunAsync(connectionString, sqlitePath, output, Ct);

            Assert.Equal(1, exitCode);
            Assert.Contains("already holds data", output.ToString(), StringComparison.Ordinal);

            // And nothing was touched: still one stack, not two.
            await using var db = Context(connectionString);
            Assert.Equal(1, await db.Stacks.CountAsync(Ct));
        } finally {
            PostgresTestServer.Drop(connectionString);
            File.Delete(sqlitePath);
        }
    }

    /// <summary>
    /// A source that predates ADR-0023 keeps its realms' login hostnames in <c>realms.auth_host</c>, a
    /// column the model no longer has. The conversion that used to be a migration was regenerated away
    /// by ADR-0024, so the importer is the only thing left that can carry it — and dropping it silently
    /// would leave every customer realm with no login page.
    /// </summary>
    [Fact]
    public async Task ConvertsALegacyRealmAuthHostIntoItsWatchtowerLoginRoute() {
        var sqlitePath = WritePreLoginRouteDatabase();
        var connectionString = PostgresTestServer.CreateDatabase();
        var output = new StringWriter();
        try {
            Assert.True(await SqliteImporter.RunAsync(connectionString, sqlitePath, output, Ct) == 0, output.ToString());

            await using var db = Context(connectionString);

            // The realm that had a host now has a Watchtower route, and it is its login route.
            var acme = await db.Realms.AsNoTracking()
                .Include(r => r.LoginRoute)
                .SingleAsync(r => r.Slug == "acme", Ct);
            Assert.NotNull(acme.LoginRoute);
            Assert.Equal(RouteTarget.Watchtower, acme.LoginRoute.Target);
            // Lowercased on the way in: the source column was never normalized, route domains are.
            Assert.Equal("login.acme.invalid", acme.LoginRoute.Domain);
            Assert.Equal(acme.Id, acme.LoginRoute.RealmId);
            Assert.Null(acme.LoginRoute.StackId);
            Assert.True(acme.LoginRoute.TlsEnabled);
            Assert.Equal(DomainKind.Managed, acme.LoginRoute.Kind);
            Assert.Equal(AccessMode.Public, acme.LoginRoute.AccessMode);
            Assert.Equal(RouteStatus.Pending, acme.LoginRoute.Status);

            // The realm that never had one still has none — nothing was invented for it.
            Assert.Null((await db.Realms.AsNoTracking().SingleAsync(r => r.Slug == "pending", Ct)).LoginRouteId);

            // The pre-existing application route is untouched, and reads as a service route because the
            // source had no `target` column at all.
            var app = await db.Routes.AsNoTracking().SingleAsync(r => r.Domain == "app.example.invalid", Ct);
            Assert.Equal(RouteTarget.Service, app.Target);
            Assert.NotNull(app.StackId);
            Assert.Null(app.RealmId);

            Assert.Contains("converted 1 legacy realm login host(s)", output.ToString(), StringComparison.Ordinal);
            // And the drop itself was reported rather than swallowed.
            Assert.Contains(
                "warning: realms.auth_host exists in the source but not in the model",
                output.ToString(),
                StringComparison.Ordinal);
        } finally {
            PostgresTestServer.Drop(connectionString);
            File.Delete(sqlitePath);
        }
    }

    /// <summary>
    /// The one case the conversion must decline: the hostname a realm named is already an application's
    /// route. Re-pointing it at the management plane would take the application off the internet.
    /// </summary>
    [Fact]
    public async Task LeavesALegacyAuthHostAlreadyServedByAServiceRouteAlone() {
        var sqlitePath = WritePreLoginRouteDatabase(acmeAuthHost: "app.example.invalid");
        var connectionString = PostgresTestServer.CreateDatabase();
        var output = new StringWriter();
        try {
            Assert.True(await SqliteImporter.RunAsync(connectionString, sqlitePath, output, Ct) == 0, output.ToString());

            await using var db = Context(connectionString);

            var route = await db.Routes.AsNoTracking().SingleAsync(r => r.Domain == "app.example.invalid", Ct);
            Assert.Equal(RouteTarget.Service, route.Target);
            Assert.Equal("web", route.ServiceName);

            Assert.Null((await db.Realms.AsNoTracking().SingleAsync(r => r.Slug == "acme", Ct)).LoginRouteId);
            Assert.Contains("already a service route", output.ToString(), StringComparison.Ordinal);
        } finally {
            PostgresTestServer.Drop(connectionString);
            File.Delete(sqlitePath);
        }
    }

    [Fact]
    public async Task ReportsAMissingSourceFileInsteadOfThrowing() {
        var output = new StringWriter();
        var exitCode = await SqliteImporter.RunAsync(
            "Host=127.0.0.1;Database=unused;Username=unused;Password=unused",
            Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db"),
            output,
            Ct);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", output.ToString(), StringComparison.Ordinal);
    }

    private static WatchtowerDbContext Context(string connectionString) =>
        new(new DbContextOptionsBuilder<WatchtowerDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options);

    /// <summary>
    /// Writes a SQLite file shaped like a pre-ADR-0024 installation: a subset of the tables, in the
    /// storage types EF's SQLite provider used, with ids that are not 1..n so the copy has to carry them
    /// rather than regenerate them.
    /// </summary>
    internal static string WriteLegacyDatabase() {
        var path = Path.Combine(Path.GetTempPath(), $"watchtower-legacy-{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE realms (
                id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, slug TEXT NOT NULL,
                login_route_id INTEGER NULL, is_system INTEGER NOT NULL, created_at TEXT NOT NULL);
            INSERT INTO realms (id, name, slug, login_route_id, is_system, created_at) VALUES
                (1, 'Operator', 'operator', NULL, 1, '{LegacyTimestamp}'),
                (2, 'Acme', 'acme', NULL, 0, '{LegacyTimestamp}');

            CREATE TABLE stacks (
                id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, repository_url TEXT NOT NULL,
                compose_file_path TEXT NOT NULL, branch TEXT NOT NULL, compose_project_name TEXT NOT NULL,
                auto_deploy_mode TEXT NOT NULL, webhook_enabled INTEGER NOT NULL,
                app_api_enabled INTEGER NOT NULL, backup_enabled INTEGER NOT NULL,
                backup_stop_containers INTEGER NOT NULL, created_at TEXT NOT NULL);
            INSERT INTO stacks (id, name, repository_url, compose_file_path, branch, compose_project_name,
                                auto_deploy_mode, webhook_enabled, app_api_enabled, backup_enabled,
                                backup_stop_containers, created_at)
            VALUES (7, 'demo', 'https://example.invalid/demo.git', 'docker-compose.yml', 'main', 'demo',
                    'OnChange', 1, 0, 0, 1, '{LegacyTimestamp}');

            -- Column order deliberately unlike the model's, and identity_header_mode deliberately absent:
            -- both are what a source written by an older migration history looks like.
            CREATE TABLE routes (
                id INTEGER PRIMARY KEY AUTOINCREMENT, domain TEXT NOT NULL, stack_id INTEGER NULL,
                service_name TEXT NOT NULL, container_port INTEGER NOT NULL, tls_enabled INTEGER NOT NULL,
                is_primary INTEGER NOT NULL, kind TEXT NOT NULL, access_mode TEXT NOT NULL,
                status TEXT NOT NULL, target TEXT NOT NULL, realm_id INTEGER NULL, created_at TEXT NOT NULL);
            INSERT INTO routes (id, domain, stack_id, service_name, container_port, tls_enabled, is_primary,
                                kind, access_mode, status, target, realm_id, created_at)
            VALUES (4, 'app.example.invalid', 7, 'web', 8080, 1, 1, 'Managed', 'Authenticated', 'Active',
                    'Service', NULL, '{LegacyTimestamp}');

            CREATE TABLE users (
                id INTEGER PRIMARY KEY AUTOINCREMENT, realm_id INTEGER NOT NULL, user_name TEXT NOT NULL,
                normalized_user_name TEXT NOT NULL, email TEXT NULL, password_hash TEXT NOT NULL,
                is_admin INTEGER NOT NULL, disabled INTEGER NOT NULL, access_failed_count INTEGER NOT NULL,
                lockout_end TEXT NULL, two_factor_enabled INTEGER NOT NULL,
                authenticator_key TEXT NULL, security_stamp TEXT NOT NULL, concurrency_stamp TEXT NOT NULL,
                created_at TEXT NOT NULL);
            INSERT INTO users (id, realm_id, user_name, normalized_user_name, email, password_hash,
                               is_admin, disabled, access_failed_count, lockout_end, two_factor_enabled,
                               authenticator_key, security_stamp, concurrency_stamp, created_at)
            VALUES (3, 2, 'legacy', 'LEGACY', 'legacy@example.invalid', 'hash-value', 1, 0, 2, NULL, 0,
                    NULL, 'stamp-s', 'stamp-c', '{LegacyTimestamp}');

            CREATE TABLE elarion_settings (
                kind TEXT NOT NULL, owner TEXT NOT NULL, "key" TEXT NOT NULL, value TEXT NULL,
                updated_on_utc TEXT NOT NULL, version INTEGER NOT NULL,
                PRIMARY KEY (kind, owner, "key"));
            INSERT INTO elarion_settings VALUES
                ('global', '', 'Watchtower:Auth:Enabled', 'true', '{LegacyTimestamp}', 1);

            -- A table the model no longer has: skipped with a note rather than failing the import.
            CREATE TABLE legacy_leftovers (id INTEGER PRIMARY KEY, note TEXT);
            INSERT INTO legacy_leftovers VALUES (1, 'gone');
            """;
        command.ExecuteNonQuery();
        SqliteConnection.ClearPool(connection);
        return path;
    }

    /// <summary>
    /// A SQLite file from *before* ADR-0023: realms keep their login hostname in an <c>auth_host</c>
    /// column and have no <c>login_route_id</c>, and the routes table has no <c>target</c> column, so
    /// every route in it is a service route by construction.
    /// </summary>
    /// <param name="acmeAuthHost">The hostname the `acme` realm names. Defaults to one nothing else uses.</param>
    private static string WritePreLoginRouteDatabase(string acmeAuthHost = "Login.ACME.invalid") {
        var path = Path.Combine(Path.GetTempPath(), $"watchtower-prelogin-{Guid.NewGuid():N}.db");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TABLE realms (
                id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, slug TEXT NOT NULL,
                auth_host TEXT NULL, is_system INTEGER NOT NULL, created_at TEXT NOT NULL);
            INSERT INTO realms (id, name, slug, auth_host, is_system, created_at) VALUES
                (1, 'Operator', 'operator', NULL, 1, '{LegacyTimestamp}'),
                (2, 'Acme', 'acme', '{acmeAuthHost}', 0, '{LegacyTimestamp}'),
                (3, 'Pending', 'pending', NULL, 0, '{LegacyTimestamp}');

            CREATE TABLE stacks (
                id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, repository_url TEXT NOT NULL,
                compose_file_path TEXT NOT NULL, branch TEXT NOT NULL, compose_project_name TEXT NOT NULL,
                auto_deploy_mode TEXT NOT NULL, webhook_enabled INTEGER NOT NULL,
                app_api_enabled INTEGER NOT NULL, backup_enabled INTEGER NOT NULL,
                backup_stop_containers INTEGER NOT NULL, created_at TEXT NOT NULL);
            INSERT INTO stacks (id, name, repository_url, compose_file_path, branch, compose_project_name,
                                auto_deploy_mode, webhook_enabled, app_api_enabled, backup_enabled,
                                backup_stop_containers, created_at)
            VALUES (7, 'demo', 'https://example.invalid/demo.git', 'docker-compose.yml', 'main', 'demo',
                    'Off', 1, 0, 0, 1, '{LegacyTimestamp}');

            -- No `target` and no `realm_id`: this schema predates both.
            CREATE TABLE routes (
                id INTEGER PRIMARY KEY AUTOINCREMENT, domain TEXT NOT NULL, stack_id INTEGER NOT NULL,
                service_name TEXT NOT NULL, container_port INTEGER NOT NULL, tls_enabled INTEGER NOT NULL,
                is_primary INTEGER NOT NULL, kind TEXT NOT NULL, access_mode TEXT NOT NULL,
                status TEXT NOT NULL, created_at TEXT NOT NULL);
            INSERT INTO routes (id, domain, stack_id, service_name, container_port, tls_enabled,
                                is_primary, kind, access_mode, status, created_at)
            VALUES (4, 'app.example.invalid', 7, 'web', 8080, 1, 1, 'Managed', 'Authenticated', 'Active',
                    '{LegacyTimestamp}');
            """;
        command.ExecuteNonQuery();
        SqliteConnection.ClearPool(connection);
        return path;
    }
}
