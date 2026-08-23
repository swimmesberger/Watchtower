using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the realm data model (docs/central-auth/design.md §13): the seeded operator realm, the
/// per-realm credential space the scoped indexes and <see cref="WatchtowerUserStore"/> together create,
/// and the check constraints and filtered indexes that hold the two route kinds apart.
/// </summary>
public sealed class RealmsDataModelTests {
    private const string Password = "correct-horse-battery";

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
        Assert.IsType<PostgresException>(refused.InnerException);
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
        Assert.IsType<PostgresException>(refused.InnerException);
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
        Assert.IsType<PostgresException>(clash.InnerException);
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
    //
    // Five tests used to live here: two that stepped a database up to the last pre-realm migration and
    // back down again, and three that did the same for the auth_host -> Watchtower route conversion.
    // They were about SQLite's table-rebuild behaviour — that adding a foreign-key column regenerates
    // the table from the model snapshot, and that a rebuild forgetting a column loses its data silently.
    // ADR-0024 regenerated the migration history for PostgreSQL as a single InitialPostgreSql, so there
    // is no pre-realm migration to step to and no rebuild to distrust; an existing installation is
    // carried across by `--import-sqlite`, whose own round-trip test replaces them. What those tests
    // asserted about the *model* — the check constraints, the filtered unique indexes, the per-realm
    // credential space, the seeded operator realm — is asserted above, against a real database.

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
