using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the realm data model (docs/central-auth/design.md §13): the seeded operator realm, the
/// per-realm credential space the scoped indexes and <see cref="WatchtowerUserStore"/> together create,
/// and — the part an upgrade depends on — that the <c>AddRealms</c> migration carries every pre-existing
/// row into realm 1 instead of dropping it.
/// </summary>
public sealed class RealmsDataModelTests {
    private const string Password = "correct-horse-battery";

    /// <summary>Domain pattern of the pre-realm template seeded by the upgrade test.</summary>
    private const string LegacyDomainPattern = "{tenant}.example.invalid";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // -- The seeded system realm -----------------------------------------------------------------

    [Fact]
    public async Task Migration_SeedsExactlyOneSystemRealm_WithNoAuthHostOfItsOwn() {
        using var host = AuthTestHost.Start();

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var realm = await db.Realms.SingleAsync(Ct);

        Assert.True(realm.IsSystem);
        Assert.Equal(Realm.SystemRealmId, realm.Id);
        Assert.Equal(Realm.SystemRealmSlug, realm.Slug);
        Assert.Equal(Realm.SystemRealmName, realm.Name);
        // Never a stored host: the operator login page is found through Watchtower:Auth:Host, so
        // authentication does not depend on a row to locate itself.
        Assert.Null(realm.AuthHost);
    }

    [Fact]
    public async Task SystemRealm_IsResolvedByReadingTheFlag_NotByAssumingTheId() {
        using var host = AuthTestHost.Start();

        await using var scope = host.Services.CreateAsyncScope();
        var realms = scope.ServiceProvider.GetRequiredService<RealmResolver>();
        var system = await realms.SystemRealmAsync(Ct);

        Assert.True(system.IsSystem);
        Assert.Equal(Realm.SystemRealmSlug, system.Slug);
    }

    // -- Host resolution -------------------------------------------------------------------------

    [Fact]
    public async Task ResolveByHost_FindsTheRealmThatClaimsIt() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var realms = scope.ServiceProvider.GetRequiredService<RealmResolver>();

        // Case-insensitively: host names are.
        Assert.Equal(acme, (await realms.ResolveByHostAsync("LOGIN.acme.INVALID", Ct)).Id);
    }

    /// <summary>
    /// Everything that is not a realm's login host is the operator realm — the configured auth host, the
    /// published port, a bare IP, and a value that is not a host name at all. Fail-safe rather than
    /// fail-closed on purpose: the failure mode of the other direction is a lockout, and the realm an
    /// unknown host resolves to is only which login page is shown, never who may sign in there.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("watchtower.example.invalid")]
    [InlineData("10.0.0.4")]
    [InlineData("login.acme.invalid/evil")]
    [InlineData("evil@login.acme.invalid")]
    public async Task ResolveByHost_FallsBackToTheSystemRealm(string? candidate) {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", "watchtower.example.invalid"));
        await host.AddRealmAsync("acme", "login.acme.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var realms = scope.ServiceProvider.GetRequiredService<RealmResolver>();

        Assert.True((await realms.ResolveByHostAsync(candidate, Ct)).IsSystem);
    }

    [Fact]
    public async Task LoginHost_IsConfigurationForTheSystemRealm_AndTheRowForAnyOther() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", "Watchtower.Example.Invalid"));
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var pending = await host.AddRealmAsync("pending");

        await using var scope = host.Services.CreateAsyncScope();
        var realms = scope.ServiceProvider.GetRequiredService<RealmResolver>();

        Assert.Equal("watchtower.example.invalid", realms.LoginHostFor(await realms.SystemRealmAsync(Ct)));
        Assert.Equal("login.acme.invalid", realms.LoginHostFor((await realms.FindAsync(acme, Ct))!));
        // Created before its DNS exists: no host, and therefore nowhere to send a challenge.
        Assert.Null(realms.LoginHostFor((await realms.FindAsync(pending, Ct))!));
    }

    // -- Route → realm ---------------------------------------------------------------------------

    [Fact]
    public async Task RouteRealm_ComesFromTheStacksTemplate_AndIsTheSystemRealmForAStandaloneStack() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var template = await host.AddRealmTemplateAsync("shop", acme);
        var tenant = await host.AddRouteAsync("one.shop.example.invalid", AccessMode.Authenticated, template);
        var standalone = await host.AddRouteAsync("solo.example.invalid", AccessMode.Authenticated);

        await using var scope = host.Services.CreateAsyncScope();
        var realms = scope.ServiceProvider.GetRequiredService<RealmResolver>();

        Assert.Equal(acme, (await realms.RealmForRouteAsync(tenant, Ct)).Id);
        Assert.True((await realms.RealmForRouteAsync(standalone, Ct)).IsSystem);
    }

    // -- The per-realm credential space ----------------------------------------------------------

    [Fact]
    public async Task TheSameUserName_MayExistInTwoRealms() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");

        var operatorAdmin = await host.AddUserAsync("admin");
        var acmeAdmin = await host.AddUserAsync("admin", realmId: acme);

        Assert.NotEqual(operatorAdmin, acmeAdmin);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal(2, await db.Users.CountAsync(u => u.NormalizedUserName == "ADMIN", Ct));
    }

    [Fact]
    public async Task ADuplicateNameWithinOneRealm_IsStillRefused() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        await host.AddUserAsync("admin", realmId: acme);

        await using var scope = host.Services.CreateAsyncScope();
        // The realm the second creation is aimed at is the one Identity's duplicate check is answered
        // about — that is the whole contract of IRealmContext.
        scope.ServiceProvider.GetRequiredService<IRealmContext>().SetRealm(acme);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var duplicate = AuthTestHost.NewUser("ADMIN");
        duplicate.RealmId = acme;

        var result = await users.CreateAsync(duplicate, Password);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == nameof(IdentityErrorDescriber.DuplicateUserName));
    }

    /// <summary>
    /// The index, not the handler: a writer that bypasses the pre-check entirely still cannot land two
    /// accounts with one name in one realm — while the same pair across two realms is simply two accounts.
    /// </summary>
    [Fact]
    public async Task TheUniqueIndex_IsScopedToTheRealm() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        db.Users.Add(Raw("dana", Realm.SystemRealmId));
        db.Users.Add(Raw("dana", acme));
        await db.SaveChangesAsync(Ct);

        db.Users.Add(Raw("dana", acme));
        var clash = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.IsType<SqliteException>(clash.InnerException);
    }

    [Fact]
    public async Task GroupNames_AreUniqueWithinARealm_NotAcrossThem() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");

        await host.AddGroupAsync("staff");
        await host.AddGroupInRealmAsync("staff", acme);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Equal(2, await db.Groups.CountAsync(g => g.NormalizedName == "STAFF", Ct));

        db.Groups.Add(new Group { RealmId = acme, Name = "Staff", NormalizedName = "STAFF" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task ALoginNameIsOnlyVisibleWithinItsOwnRealm() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        await host.AddUserAsync("carol", realmId: acme);

        await using (var scope = host.Services.CreateAsyncScope()) {
            // Default context: the operator realm, which has no carol.
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.Null(await users.FindByNameAsync("carol"));
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            scope.ServiceProvider.GetRequiredService<IRealmContext>().SetRealm(acme);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var carol = await users.FindByNameAsync("carol");
            Assert.NotNull(carol);
            Assert.Equal(acme, carol.RealmId);
            Assert.True(await users.CheckPasswordAsync(carol, Password));
        }
    }

    [Fact]
    public async Task ARealmStillReferencedByAnAccount_CannotBeDeletedByTheDatabase() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        await host.AddUserAsync("carol", realmId: acme);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.Realms.Remove(await db.Realms.SingleAsync(r => r.Id == acme, Ct));

        // Restrict, not cascade: the handler refuses first, and the schema refuses regardless.
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    // -- The upgrade path ------------------------------------------------------------------------

    /// <summary>
    /// The landmine this whole migration is arranged around: adding a foreign-key column on SQLite makes
    /// EF rebuild the table from the model snapshot and copy the rows across with an
    /// <c>INSERT … SELECT</c>. A rebuild that forgot a column would silently drop that column's data, and
    /// the schema afterwards would still look right. So this migrates a database to the last pre-realm
    /// migration, writes a row into each affected table by hand, migrates the rest of the way, and reads
    /// every field back.
    /// </summary>
    [Fact]
    public async Task AddRealms_CarriesExistingRowsIntoTheOperatorRealm_WithNothingLost() {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        await using var db = new WatchtowerDbContext(
            new DbContextOptionsBuilder<WatchtowerDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options);

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("AddGroups", Ct);

        // Written as SQL, not through the model: the entities already carry realm_id, and the point is to
        // produce rows exactly as a pre-realm instance would have. The domain pattern travels as a
        // parameter because ExecuteSqlRaw runs its text through string.Format, which would read the
        // literal "{tenant}" placeholder as a format item.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO users
                (user_name, normalized_user_name, email, password_hash, is_admin, disabled,
                 access_failed_count, lockout_end, security_stamp, concurrency_stamp, created_at)
            VALUES
                ('legacy', 'LEGACY', 'legacy@example.invalid', 'hash-value', 1, 0,
                 3, NULL, 'stamp-s', 'stamp-c', '2026-01-02 03:04:05+00:00');
            INSERT INTO groups (name, normalized_name, created_at)
            VALUES ('Legacy Staff', 'LEGACY STAFF', '2026-01-02 03:04:05+00:00');
            INSERT INTO stack_templates
                (name, repository_url, compose_file_path, branch, credential_id, domain_pattern,
                 target_service_name, target_port, created_at)
            VALUES
                ('legacy-shop', 'https://example.invalid/shop.git', 'docker-compose.yml', 'main', NULL,
                 {0}, 'web', 3000, '2026-01-02 03:04:05+00:00');
            """, [LegacyDomainPattern], Ct);

        await migrator.MigrateAsync(cancellationToken: Ct);

        var realm = await db.Realms.AsNoTracking().SingleAsync(Ct);
        Assert.True(realm.IsSystem);

        var user = await db.Users.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(realm.Id, user.RealmId);
        Assert.Equal("legacy", user.UserName);
        Assert.Equal("LEGACY", user.NormalizedUserName);
        Assert.Equal("legacy@example.invalid", user.Email);
        Assert.Equal("hash-value", user.PasswordHash);
        Assert.True(user.IsAdmin);
        Assert.False(user.Disabled);
        Assert.Equal(3, user.AccessFailedCount);
        Assert.Null(user.LockoutEnd);
        Assert.Equal("stamp-s", user.SecurityStamp);
        Assert.Equal("stamp-c", user.ConcurrencyStamp);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), user.CreatedAt);

        var group = await db.Groups.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(realm.Id, group.RealmId);
        Assert.Equal("Legacy Staff", group.Name);
        Assert.Equal("LEGACY STAFF", group.NormalizedName);

        var template = await db.StackTemplates.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(realm.Id, template.RealmId);
        Assert.Equal("legacy-shop", template.Name);
        Assert.Equal("https://example.invalid/shop.git", template.RepositoryUrl);
        Assert.Equal("docker-compose.yml", template.ComposeFilePath);
        Assert.Equal("main", template.Branch);
        Assert.Null(template.CredentialId);
        Assert.Equal(LegacyDomainPattern, template.DomainPattern);
        Assert.Equal("web", template.TargetServiceName);
        Assert.Equal(3000, template.TargetPort);
    }

    /// <summary>
    /// The rollback has to run, not merely compile. On SQLite a foreign key is dropped by rebuilding the
    /// table, so while the <c>realm_id</c> columns still exist every row is still pointing at realm 1
    /// through a RESTRICT constraint — removing the <c>realms</c> table before the columns is refused, and
    /// the migration is then irreversible with no way to notice short of trying it. So this migrates all
    /// the way up, seeds an operator-realm account, and migrates back down.
    /// </summary>
    [Fact]
    public async Task AddRealms_CanBeRolledBack_WithTheOperatorRealmsRowsIntact() {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        await using var db = new WatchtowerDbContext(
            new DbContextOptionsBuilder<WatchtowerDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options);

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(cancellationToken: Ct);

        db.Users.Add(Raw("legacy", Realm.SystemRealmId));
        db.Groups.Add(new Group {
            RealmId = Realm.SystemRealmId, Name = "Legacy Staff", NormalizedName = "LEGACY STAFF",
        });
        await db.SaveChangesAsync(Ct);

        await migrator.MigrateAsync("AddGroups", Ct);

        // The realm columns and the table are gone…
        Assert.False(await ColumnExistsAsync(db, "users", "realm_id"));
        Assert.False(await ColumnExistsAsync(db, "groups", "realm_id"));
        Assert.False(await ColumnExistsAsync(db, "stack_templates", "realm_id"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'realms'"));

        // …and the operator realm's rows came back with them, under the v1 global unique index.
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM users WHERE normalized_user_name = 'LEGACY'"));
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) FROM groups WHERE normalized_name = 'LEGACY STAFF'"));
    }

    /// <summary>Whether <paramref name="table"/> currently has <paramref name="column"/>.</summary>
    private static async Task<bool> ColumnExistsAsync(WatchtowerDbContext db, string table, string column) {
        // pragma_table_info as a table-valued function: the table name cannot be parameterised in DDL
        // context, and both arguments here are test literals rather than input.
        var count = await ScalarAsync(
            db, $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'");
        return count > 0;
    }

    private static async Task<long> ScalarAsync(WatchtowerDbContext db, string sql) {
        await db.Database.OpenConnectionAsync(Ct);
        try {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
        } finally {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>A user row shaped for a direct insert, bypassing <c>UserManager</c> entirely.</summary>
    private static User Raw(string userName, int realmId) => new() {
        RealmId = realmId,
        UserName = userName,
        NormalizedUserName = userName.ToUpperInvariant(),
        PasswordHash = "hash",
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
    };
}
