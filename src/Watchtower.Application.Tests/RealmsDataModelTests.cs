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
    public async Task Migration_SeedsExactlyOneSystemRealm_WithNoLoginRouteOfItsOwn() {
        using var host = AuthTestHost.Start();

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var realm = await db.Realms.SingleAsync(Ct);

        Assert.True(realm.IsSystem);
        Assert.Equal(Realm.SystemRealmId, realm.Id);
        Assert.Equal(Realm.SystemRealmSlug, realm.Slug);
        Assert.Equal(Realm.SystemRealmName, realm.Name);
        // A fresh install has no routes at all, so the operator realm starts with no login route and
        // falls back to Watchtower:Auth:Host until one is created (ADR-0023).
        Assert.Null(realm.LoginRouteId);
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
    public async Task LoginHost_ComesFromTheLoginRoute_WithAuthHostAsTheSystemRealmsFallback() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", "Watchtower.Example.Invalid"));
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var pending = await host.AddRealmAsync("pending");

        await using var scope = host.Services.CreateAsyncScope();
        var realms = scope.ServiceProvider.GetRequiredService<RealmResolver>();

        // No login route on the system realm yet, so the configured fallback answers (ADR-0023).
        Assert.Equal(
            "watchtower.example.invalid",
            await realms.LoginHostForAsync(await realms.SystemRealmAsync(Ct), Ct));
        Assert.Equal("login.acme.invalid", await realms.LoginHostForAsync((await realms.FindAsync(acme, Ct))!, Ct));
        // Created before its DNS exists: no login route, no fallback (it is not the system realm), and
        // therefore nowhere to send a challenge.
        Assert.Null(await realms.LoginHostForAsync((await realms.FindAsync(pending, Ct))!, Ct));
    }

    [Fact]
    public async Task ASystemRealmLoginRoute_TakesPrecedenceOverTheConfiguredFallback() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", "fallback.example.invalid"));
        await host.AddWatchtowerRouteAsync("ui.example.invalid", makeLoginRoute: true);

        await using var scope = host.Services.CreateAsyncScope();
        var realms = scope.ServiceProvider.GetRequiredService<RealmResolver>();

        // The route is the answer whenever there is one; Auth:Host is what fills in while there is not.
        Assert.Equal("ui.example.invalid", await realms.LoginHostForAsync(await realms.SystemRealmAsync(Ct), Ct));
    }

    [Fact]
    public async Task ResolveByHost_ReadsTheWatchtowerRouteTable() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme");
        // A second Watchtower hostname for the realm, not its login route: which population a visitor
        // arriving on a hostname belongs to is decided by the route, not by the designation.
        await host.AddWatchtowerRouteAsync("portal.acme.invalid", acme);

        await using var scope = host.Services.CreateAsyncScope();
        var realms = scope.ServiceProvider.GetRequiredService<RealmResolver>();

        Assert.Equal(acme, (await realms.ResolveByHostAsync("PORTAL.acme.INVALID", Ct)).Id);
    }

    [Fact]
    public async Task ResolveByHost_IgnoresAServiceRouteOnTheSameKindOfName() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var template = await host.AddRealmTemplateAsync("shop", acme);
        await host.AddRouteAsync("app.acme.invalid", AccessMode.Authenticated, template);

        await using var scope = host.Services.CreateAsyncScope();
        var realms = scope.ServiceProvider.GetRequiredService<RealmResolver>();

        // A forwarded application's hostname is not a login page, whatever realm the app belongs to —
        // arriving there is not "sign in to acme", so it falls back like any unrecognised host.
        Assert.True((await realms.ResolveByHostAsync("app.acme.invalid", Ct)).IsSystem);
    }

    [Fact]
    public async Task ARealmStillServingWatchtowerOnAHostname_CannotBeDeletedByTheDatabase() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.Realms.Remove(await db.Realms.SingleAsync(r => r.Id == acme, Ct));

        // Restrict on routes.realm_id: realms.delete refuses first, and the schema refuses regardless —
        // a realm delete must never take a live public hostname with it (ADR-0023).
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task DeletingALoginRoute_LeavesTheRealmWithoutOne_RatherThanFailing() {
        using var host = AuthTestHost.Start();
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Routes.Where(r => r.Domain == "login.acme.invalid").ExecuteDeleteAsync(Ct);

        // ON DELETE SET NULL: removing the hostname is a legitimate act with a visible consequence, not
        // something the schema refuses.
        var realm = await db.Realms.AsNoTracking().SingleAsync(r => r.Id == acme, Ct);
        Assert.Null(realm.LoginRouteId);
    }

    /// <summary>
    /// The check constraint, not the handlers: a writer that bypasses <c>proxy.createRoute</c> entirely
    /// still cannot store the two shapes that would break the model — a Watchtower route that is gated,
    /// and one that also points at a stack.
    /// </summary>
    [Fact]
    public async Task TheRouteTargetConstraint_RefusesAGatedWatchtowerRoute() {
        using var host = AuthTestHost.Start();

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.Routes.Add(new Entities.Route {
            Target = RouteTarget.Watchtower,
            RealmId = Realm.SystemRealmId,
            Domain = "ui.example.invalid",
            ServiceName = string.Empty,
            AccessMode = AccessMode.Authenticated,
        });

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.IsType<SqliteException>(refused.InnerException);
    }

    [Fact]
    public async Task TheRouteTargetConstraint_RefusesAServiceRouteCarryingARealm() {
        using var host = AuthTestHost.Start();

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = "demo", RepositoryUrl = "https://example.invalid/demo.git",
            ComposeFilePath = "docker-compose.yml", Branch = "main", ComposeProjectName = "demo",
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);

        db.Routes.Add(new Entities.Route {
            Target = RouteTarget.Service,
            StackId = stack.Id,
            // A service route inherits its realm from its stack's category; a second answer stored here
            // is exactly the disagreement the model exists to prevent.
            RealmId = Realm.SystemRealmId,
            Domain = "app.example.invalid",
            ServiceName = "web",
            ContainerPort = 8080,
        });

        var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        Assert.IsType<SqliteException>(refused.InnerException);
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

    // -- ADR-0023: auth_host → Watchtower route --------------------------------------------------

    /// <summary>
    /// The conversion the <c>ConvertLoginHostsToRoutes</c> migration carries. It has to happen inside a
    /// migration because <c>realms.auth_host</c> must be read before it is dropped, and the ordering that
    /// makes that work — raw SQL keeps its position while table rebuilds are hoisted to the end — is EF
    /// SQLite behaviour rather than something the C# states. So this seeds a legacy database and migrates
    /// through it for real.
    /// </summary>
    [Fact]
    public async Task ConvertLoginHostsToRoutes_TurnsEachRealmsAuthHostIntoItsLoginRoute() {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = LegacyContext(connection);

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("AddBackupCronSchedule", Ct);

        // Two realms with a stored host, one without, and a service route on an unrelated domain — the
        // pre-ADR-0023 shape exactly as an upgraded instance would hold it.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO realms (name, slug, auth_host, is_system, created_at) VALUES
                ('Acme', 'acme', 'Login.ACME.invalid', 0, '2026-01-02 03:04:05+00:00'),
                ('Contoso', 'contoso', 'login.contoso.invalid', 0, '2026-01-02 03:04:05+00:00'),
                ('Pending', 'pending', NULL, 0, '2026-01-02 03:04:05+00:00');
            INSERT INTO stacks (name, repository_url, compose_file_path, branch, compose_project_name,
                                auto_deploy_mode, app_api_enabled, backup_enabled,
                                backup_stop_containers, webhook_enabled, created_at)
            VALUES ('demo', 'https://example.invalid/demo.git', 'docker-compose.yml', 'main', 'demo',
                    'Off', 0, 0, 0, 0, '2026-01-02 03:04:05+00:00');
            INSERT INTO routes (stack_id, domain, service_name, container_port, tls_enabled, is_primary,
                                kind, access_mode, identity_header_mode, status, created_at)
            VALUES ((SELECT id FROM stacks WHERE name = 'demo'), 'app.example.invalid', 'web', 8080, 1, 1,
                    'Managed', 'Authenticated', 'None', 'Active', '2026-01-02 03:04:05+00:00');
            """, Ct);

        await migrator.MigrateAsync(cancellationToken: Ct);

        var acme = await db.Realms.AsNoTracking().Include(r => r.LoginRoute).SingleAsync(r => r.Slug == "acme", Ct);
        Assert.NotNull(acme.LoginRoute);
        // Lowercased on the way in: a route domain is stored normalised, and the old column was not.
        Assert.Equal("login.acme.invalid", acme.LoginRoute.Domain);
        Assert.Equal(RouteTarget.Watchtower, acme.LoginRoute.Target);
        Assert.Null(acme.LoginRoute.StackId);
        Assert.Equal(acme.Id, acme.LoginRoute.RealmId);
        Assert.True(acme.LoginRoute.TlsEnabled);
        Assert.Equal(DomainKind.Managed, acme.LoginRoute.Kind);
        Assert.Equal(AccessMode.Public, acme.LoginRoute.AccessMode);
        Assert.Equal(RouteStatus.Pending, acme.LoginRoute.Status);
        // Written in EF's own SQLite shape for a DateTimeOffset, so it materialises here at all and
        // sorts as text against rows the application writes. Reading it back is the assertion; a format
        // EF could not parse would have thrown on the query above.
        Assert.Equal(TimeSpan.Zero, acme.LoginRoute.CreatedAt.Offset);
        Assert.NotEqual(default, acme.LoginRoute.CreatedAt);

        var contoso = await db.Realms.AsNoTracking().Include(r => r.LoginRoute).SingleAsync(r => r.Slug == "contoso", Ct);
        Assert.Equal("login.contoso.invalid", contoso.LoginRoute!.Domain);

        // A realm that never had a host still has none — nothing was invented for it.
        var pending = await db.Realms.AsNoTracking().SingleAsync(r => r.Slug == "pending", Ct);
        Assert.Null(pending.LoginRouteId);

        // The pre-existing application route is untouched and reads as a service route.
        var app = await db.Routes.AsNoTracking().SingleAsync(r => r.Domain == "app.example.invalid", Ct);
        Assert.Equal(RouteTarget.Service, app.Target);
        Assert.Null(app.RealmId);
        Assert.NotNull(app.StackId);
        Assert.Equal(AccessMode.Authenticated, app.AccessMode);

        // And nothing was created for the operator realm, whose host is configuration rather than a
        // column — LoginHostConversion handles that half on the next start.
        var system = await db.Realms.AsNoTracking().SingleAsync(r => r.IsSystem, Ct);
        Assert.Null(system.LoginRouteId);
    }

    /// <summary>
    /// The one case where the conversion has to decline: the hostname a realm named is already a service
    /// route. Re-pointing a domain that serves an application at the management plane would be the worst
    /// possible reading of an upgrade, so the row is left alone and the realm simply has no login route.
    /// </summary>
    [Fact]
    public async Task ConvertLoginHostsToRoutes_LeavesAnAuthHostAlreadyServedByAServiceRoute_Alone() {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = LegacyContext(connection);

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("AddBackupCronSchedule", Ct);

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO realms (name, slug, auth_host, is_system, created_at)
            VALUES ('Acme', 'acme', 'login.acme.invalid', 0, '2026-01-02 03:04:05+00:00');
            INSERT INTO stacks (name, repository_url, compose_file_path, branch, compose_project_name,
                                auto_deploy_mode, app_api_enabled, backup_enabled,
                                backup_stop_containers, webhook_enabled, created_at)
            VALUES ('demo', 'https://example.invalid/demo.git', 'docker-compose.yml', 'main', 'demo',
                    'Off', 0, 0, 0, 0, '2026-01-02 03:04:05+00:00');
            INSERT INTO routes (stack_id, domain, service_name, container_port, tls_enabled, is_primary,
                                kind, access_mode, identity_header_mode, status, created_at)
            VALUES ((SELECT id FROM stacks WHERE name = 'demo'), 'login.acme.invalid', 'web', 8080, 1, 1,
                    'Managed', 'Public', 'None', 'Active', '2026-01-02 03:04:05+00:00');
            """, Ct);

        await migrator.MigrateAsync(cancellationToken: Ct);

        var route = await db.Routes.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(RouteTarget.Service, route.Target);
        Assert.Equal("web", route.ServiceName);

        var acme = await db.Realms.AsNoTracking().SingleAsync(r => r.Slug == "acme", Ct);
        Assert.Null(acme.LoginRouteId);
    }

    [Fact]
    public async Task ConvertLoginHostsToRoutes_CanBeRolledBack_WithTheLoginHostWrittenBack() {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        await using var db = LegacyContext(connection);

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(cancellationToken: Ct);

        var realm = new Realm { Name = "Acme", Slug = "acme", CreatedAt = DateTimeOffset.UtcNow };
        db.Realms.Add(realm);
        await db.SaveChangesAsync(Ct);
        var route = new Entities.Route {
            Target = RouteTarget.Watchtower,
            RealmId = realm.Id,
            Domain = "login.acme.invalid",
            ServiceName = string.Empty,
        };
        db.Routes.Add(route);
        await db.SaveChangesAsync(Ct);
        realm.LoginRouteId = route.Id;
        await db.SaveChangesAsync(Ct);

        await migrator.MigrateAsync("AddBackupCronSchedule", Ct);

        // The old shape cannot hold a stack-less route, so the Watchtower row is gone — but the hostname
        // it carried is back in auth_host, which is the fact the old code reads.
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) FROM routes"));
        Assert.Equal(
            1,
            await ScalarAsync(db, "SELECT COUNT(*) FROM realms WHERE auth_host = 'login.acme.invalid'"));
    }

    /// <summary>
    /// A context over the raw connection, for tests that step through migrations by hand rather than
    /// through <see cref="AuthTestHost"/> (which always migrates all the way up).
    /// </summary>
    private static WatchtowerDbContext LegacyContext(SqliteConnection connection) =>
        new(new DbContextOptionsBuilder<WatchtowerDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options);

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
