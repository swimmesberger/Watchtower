using Elarion.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Watchtower.Application.Persistence;

/// <summary>
/// Central EF Core database context for Watchtower (SQLite).
/// <c>[GenerateDbSets]</c> emits a <c>DbSet&lt;T&gt;</c> per <c>[EntityConfiguration]</c> class
/// (across referenced assemblies) plus the <c>ConfigureEntities(ModelBuilder)</c> method that
/// applies every discovered configuration.
/// </summary>
[GenerateDbSets]
public sealed partial class WatchtowerDbContext(DbContextOptions<WatchtowerDbContext> options)
    : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);
        // Generated method that applies configurations from all assemblies containing entities.
        ConfigureEntities(modelBuilder);
    }
}
