using Elarion.EntityFrameworkCore;
using Elarion.Settings.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Watchtower.Application.Persistence;

/// <summary>
/// Central EF Core database context for Watchtower (SQLite).
/// <c>[GenerateDbSets]</c> emits a <c>DbSet&lt;T&gt;</c> per <c>[EntityConfiguration]</c> class
/// (across referenced assemblies) plus the <c>ConfigureEntities(ModelBuilder)</c> method that
/// applies every discovered configuration.
/// <c>[GenerateElarionSettings]</c> emits the <c>Setting</c> DbSet + entity configuration used by
/// the Elarion settings store (snake_cased columns/table to match this context's convention).
/// </summary>
[GenerateDbSets]
[GenerateElarionSettings(SnakeCase = true)]
public sealed partial class WatchtowerDbContext(DbContextOptions<WatchtowerDbContext> options)
    : DbContext(options) {
    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);
        // Generated method that applies configurations from all assemblies containing entities.
        ConfigureEntities(modelBuilder);
    }
}
