using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Tests;

/// <summary>
/// The one PostgreSQL every test in this assembly runs against, and the per-test databases carved out
/// of it (ADR-0024 — there is no in-memory backend to fall back on any more).
/// </summary>
/// <remarks>
/// <para>
/// Where the server comes from is the operator's choice, in this order:
/// </para>
/// <list type="number">
///   <item><description>
///     <c>WATCHTOWER_TEST_PG</c> — a connection string for an account that may <c>CREATE DATABASE</c>.
///     CI sets it to its <c>services: postgres</c> container, and it is also the escape hatch on a
///     workstation where Testcontainers cannot reach the container engine (see docs/contributing.md).
///   </description></item>
///   <item><description>
///     Otherwise Testcontainers starts a <c>postgres:18-alpine</c> once for the whole run, over
///     whatever <c>DOCKER_HOST</c>/<c>/var/run/docker.sock</c> points at — Docker or Podman.
///   </description></item>
/// </list>
/// <para>
/// Isolation is a database per test host, not a schema or a transaction: the code under test migrates,
/// opens its own connections and runs background work, so anything short of a real database would show
/// up as cross-test interference eventually. To keep that affordable the migrations run exactly once,
/// into a <em>template</em> database, and each test host gets <c>CREATE DATABASE … TEMPLATE …</c> — a
/// file copy inside the server rather than several hundred DDL statements over the wire.
/// </para>
/// </summary>
public static class PostgresTestServer {
    /// <summary>Connection string to a server whose account may create databases. Overrides Testcontainers.</summary>
    public const string ExternalServerVariable = "WATCHTOWER_TEST_PG";

    private static readonly Lock Gate = new();
    private static PostgreSqlContainer? _container;
    private static string? _adminConnectionString;
    private static string? _templateDatabase;

    /// <summary>
    /// Creates a fresh, already-migrated database and returns the connection string for it. The caller
    /// owns it until it passes the same string to <see cref="Drop"/>.
    /// </summary>
    public static string CreateDatabase() {
        var (admin, template) = EnsureTemplate();
        var name = $"wt_test_{Guid.NewGuid():N}";

        using var connection = new NpgsqlConnection(admin);
        connection.Open();
        // CREATE DATABASE ... TEMPLATE refuses to run while anything else is connected to the template,
        // and two of them racing is exactly that. The advisory lock serializes them across processes,
        // which matters because the two test assemblies can run at the same time against one server.
        Execute(connection, "SELECT pg_advisory_lock(4919001)");
        try {
            Execute(connection, $"""CREATE DATABASE "{name}" TEMPLATE "{template}" """);
        } finally {
            Execute(connection, "SELECT pg_advisory_unlock(4919001)");
        }
        return WithDatabase(admin, name);
    }

    /// <summary>Drops the database <paramref name="connectionString"/> names, evicting any leftover sessions.</summary>
    public static void Drop(string connectionString) {
        var name = new NpgsqlConnectionStringBuilder(connectionString).Database;
        if (string.IsNullOrEmpty(name)) return;

        // The pools hold sockets open long after the last DbContext is disposed, and PostgreSQL will not
        // drop a database anyone is still connected to.
        NpgsqlConnection.ClearPool(new NpgsqlConnection(connectionString));
        try {
            using var connection = new NpgsqlConnection(_adminConnectionString);
            connection.Open();
            Execute(connection, $"""DROP DATABASE IF EXISTS "{name}" WITH (FORCE)""");
        } catch (NpgsqlException) {
            // A leaked database costs the run nothing; failing a test over the cleanup would cost it a
            // false negative. The container (or CI's service) goes away at the end either way.
        }
    }

    /// <summary>
    /// Drops the template database and releases the container, if this run started one. Called once, by
    /// the assembly fixture.
    /// </summary>
    /// <remarks>
    /// The template has to go explicitly: on a Testcontainers run the container takes it with it, but on
    /// an external server (<see cref="ExternalServerVariable"/> — CI, or a developer's own PostgreSQL)
    /// nothing else would, and every run would leave one behind.
    /// </remarks>
    public static void Shutdown() {
        PostgreSqlContainer? container;
        string? admin;
        string? template;
        lock (Gate) {
            container = _container;
            admin = _adminConnectionString;
            template = _templateDatabase;
            _container = null;
            _templateDatabase = null;
            _adminConnectionString = null;
        }
        NpgsqlConnection.ClearAllPools();

        if (admin is not null && template is not null) {
            try {
                using var connection = new NpgsqlConnection(admin);
                connection.Open();
                Execute(connection, $"""DROP DATABASE IF EXISTS "{template}" WITH (FORCE)""");
            } catch (NpgsqlException) {
                // Same reasoning as Drop: a leaked database is cheaper than a failed run.
            }
        }

        container?.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static (string Admin, string Template) EnsureTemplate() {
        lock (Gate) {
            if (_adminConnectionString is not null && _templateDatabase is not null)
                return (_adminConnectionString, _templateDatabase);

            _adminConnectionString = ResolveAdminConnectionString();
            _templateDatabase = $"wt_template_{Guid.NewGuid():N}";

            using (var connection = new NpgsqlConnection(_adminConnectionString)) {
                connection.Open();
                Execute(connection, $"""CREATE DATABASE "{_templateDatabase}" """);
            }

            var templateConnectionString = WithDatabase(_adminConnectionString, _templateDatabase);
            var options = new DbContextOptionsBuilder<WatchtowerDbContext>()
                .UseNpgsql(templateConnectionString)
                .UseSnakeCaseNamingConvention()
                .Options;
            using (var db = new WatchtowerDbContext(options)) db.Database.Migrate();

            // Nothing may hold a connection to a template database when it is copied.
            NpgsqlConnection.ClearPool(new NpgsqlConnection(templateConnectionString));
            return (_adminConnectionString, _templateDatabase);
        }
    }

    private static string ResolveAdminConnectionString() {
        var external = Environment.GetEnvironmentVariable(ExternalServerVariable);
        if (!string.IsNullOrWhiteSpace(external)) return external;

        var container = new PostgreSqlBuilder("postgres:18-alpine")
            // Durability buys nothing for a database that lives for one test run, and fsync is most of
            // the cost of the DDL the migrations run.
            .WithCommand("-c", "fsync=off", "-c", "full_page_writes=off", "-c", "synchronous_commit=off")
            .Build();
        try {
            container.StartAsync().GetAwaiter().GetResult();
        } catch (Exception ex) {
            container.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw new InvalidOperationException(
                "Could not start a PostgreSQL test container. Watchtower's tests need a real PostgreSQL "
                + $"(ADR-0024). Either make a container engine reachable (Docker Desktop, or Podman with "
                + $"/var/run/docker.sock pointing at the podman socket), or start one yourself and set "
                + $"{ExternalServerVariable} — for example:\n"
                + "  podman run -d --name wtpg -e POSTGRES_PASSWORD=wt -p 15432:5432 postgres:18-alpine\n"
                + $"  export {ExternalServerVariable}=\"Host=127.0.0.1;Port=15432;Database=postgres;"
                + "Username=postgres;Password=wt\"\n"
                + "See docs/contributing.md.", ex);
        }
        _container = container;
        return container.GetConnectionString();
    }

    private static string WithDatabase(string connectionString, string database) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = database }.ToString();

    private static void Execute(NpgsqlConnection connection, string sql) {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// Ties <see cref="PostgresTestServer"/>'s lifetime to the test run, so the container it may have
/// started is stopped when the last test finishes rather than left to the reaper.
/// </summary>
/// <remarks>
/// An assembly fixture rather than a collection one: the server is shared by every collection, and the
/// point of the template database is that it is built once for the whole run.
/// </remarks>
public sealed class PostgresTestServerFixture : IDisposable {
    public void Dispose() => PostgresTestServer.Shutdown();
}
