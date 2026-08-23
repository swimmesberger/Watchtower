using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Watchtower.Application.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet-ef</c> to instantiate <see cref="WatchtowerDbContext"/> from
/// the CLI, with no running host and no reachable database required.
/// </summary>
/// <remarks>
/// Scaffolding a migration needs only the provider (Npgsql) and the naming convention, because those
/// are what decide the generated SQL — hence the placeholder. It is a placeholder rather than
/// <c>localhost</c> deliberately: <c>dotnet ef database update</c> would otherwise reach for whatever
/// PostgreSQL a developer happens to be running and write a schema into it. Set
/// <c>WATCHTOWER__DATABASE__CONNECTIONSTRING</c> to point the CLI at a real database on purpose.
/// (The application itself never uses this factory: the host migrates on startup through the
/// connection string it is configured with.)
/// </remarks>
internal sealed class WatchtowerDbContextFactory : IDesignTimeDbContextFactory<WatchtowerDbContext> {
    private const string PlaceholderConnectionString =
        "Host=watchtower-design-time.invalid;Database=watchtower;Username=watchtower;Password=watchtower";

    public WatchtowerDbContext CreateDbContext(string[] args) {
        var connectionString =
            Environment.GetEnvironmentVariable("WATCHTOWER__DATABASE__CONNECTIONSTRING");
        if (string.IsNullOrWhiteSpace(connectionString)) connectionString = PlaceholderConnectionString;

        var options = new DbContextOptionsBuilder<WatchtowerDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        return new WatchtowerDbContext(options);
    }
}
