using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Watchtower.Application.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet-ef</c> to instantiate <see cref="WatchtowerDbContext"/>
/// when adding migrations from the CLI (no running host required). The connection string is
/// irrelevant for migration scaffolding — only the provider (SQLite) matters.
/// </summary>
internal sealed class WatchtowerDbContextFactory : IDesignTimeDbContextFactory<WatchtowerDbContext> {
    public WatchtowerDbContext CreateDbContext(string[] args) {
        var options = new DbContextOptionsBuilder<WatchtowerDbContext>()
            .UseSqlite("Data Source=watchtower.design.db")
            .UseSnakeCaseNamingConvention()
            .Options;
        return new WatchtowerDbContext(options);
    }
}
