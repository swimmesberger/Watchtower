using Elarion.Coordination.PostgreSql;
using Elarion.EntityFrameworkCore;
using Elarion.Scheduling.EntityFrameworkCore;
using Elarion.Settings.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Watchtower.Application.Persistence;

/// <summary>
/// Central EF Core database context for Watchtower (PostgreSQL — ADR-0024).
/// <c>[GenerateDbSets]</c> emits a <c>DbSet&lt;T&gt;</c> per <c>[EntityConfiguration]</c> class
/// (across referenced assemblies) plus the <c>ConfigureEntities(ModelBuilder)</c> method that
/// applies every discovered configuration.
/// <c>[GenerateElarionSettings]</c> emits the <c>Setting</c> DbSet + entity configuration used by
/// the Elarion settings store (snake_cased columns/table to match this context's convention).
/// <c>[GenerateElarionRoleLeases]</c> and <c>[GenerateElarionSchedulerClaims]</c> do the same for the
/// two cross-instance coordination tables (ADR-0024 decisions 5 and 3): the <c>acme-issuer</c> lease
/// that decides which instance orders certificates, and the occurrence claims that keep a
/// <c>[ScheduledJob]</c> running once cluster-wide rather than once per instance.
/// </summary>
/// <remarks>
/// The ASP.NET data-protection key ring is the one table that is <em>not</em> generated: its entity
/// comes from <c>Microsoft.AspNetCore.DataProtection.EntityFrameworkCore</c>, which finds it through
/// <see cref="IDataProtectionKeyContext"/> rather than through an <c>[EntityConfiguration]</c>. The
/// DbSet below is therefore hand-declared, and coexists with <c>[GenerateDbSets]</c> precisely because
/// the generator only emits sets for types it discovered itself. Keeping the ring in the database is
/// what lets a cookie or a password-reset token minted on one instance be read on every other.
/// </remarks>
[GenerateDbSets]
[GenerateElarionSettings(SnakeCase = true)]
[GenerateElarionRoleLeases(SnakeCase = true)]
[GenerateElarionSchedulerClaims(SnakeCase = true)]
public sealed partial class WatchtowerDbContext(DbContextOptions<WatchtowerDbContext> options)
    : DbContext(options), IDataProtectionKeyContext {
    /// <summary>
    /// The ASP.NET data-protection key ring, persisted by
    /// <c>AddDataProtection().PersistKeysToDbContext&lt;WatchtowerDbContext&gt;()</c>. Mapped to
    /// <c>data_protection_keys</c> by the context's snake_case naming convention.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) {
        base.OnModelCreating(modelBuilder);
        // Generated method that applies configurations from all assemblies containing entities.
        ConfigureEntities(modelBuilder);
    }

    /// <summary>
    /// Clips every mapped <see cref="DateTimeOffset"/> to the microsecond resolution of a PostgreSQL
    /// <c>timestamptz</c> on the way in, so a stored instant is bit-for-bit the instant the application
    /// held (see <see cref="PostgresTime"/>). Without it the column silently drops the seventh fractional
    /// digit and a computed value stops comparing equal to its own round trip.
    /// </summary>
    /// <remarks>
    /// Model-wide on purpose. It reaches the Elarion-owned tables in this context too — settings, the
    /// role leases and the scheduler occurrence claims — which is what makes the rule uniform rather than
    /// a habit of Watchtower's own entities; all of those store deadlines whose sub-microsecond tail was
    /// never meaningful. EF applies a non-nullable property converter to the nullable properties of the
    /// same type as well, so <c>DateTimeOffset?</c> columns are covered by this one registration.
    /// Concurrency tokens are untouched: this context's is PostgreSQL's <c>xmin</c>, a <c>uint</c>.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) {
        base.ConfigureConventions(configurationBuilder);
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<MicrosecondPrecisionConverter>();
    }

    /// <summary>
    /// Truncating on the way to the database, identity on the way back — the read side has nothing to
    /// undo, since the column cannot return anything finer than what it was given.
    /// </summary>
    private sealed class MicrosecondPrecisionConverter()
        : ValueConverter<DateTimeOffset, DateTimeOffset>(
            stored => stored.ToMicrosecondPrecision(), read => read);
}
