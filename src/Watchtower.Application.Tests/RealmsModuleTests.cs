using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Realms;
using Watchtower.Application.Modules.Realms.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the Realms module through its real generated handler pipelines, so the
/// <c>[RequireRole("Admin")]</c> decorator and the module's own rules are exercised together: what a slug
/// and a login host may be, what the built-in operator realm refuses, and that a realm is only deletable
/// while it holds nothing.
/// </summary>
public sealed class RealmsModuleTests {
    /// <summary>
    /// Every Realms handler, added the way the generated module registration does, plus the recording
    /// proxy manager: a realm's login host is a site block, so every write here has to ask for a reload,
    /// and the real manager no-ops while the proxy is disabled (which is how every test host runs it).
    /// </summary>
    private static readonly Action<IServiceCollection> WithRealmsModule = services => {
        services.AddListRealms();
        services.AddCreateRealm();
        services.AddUpdateRealm();
        services.AddDeleteRealm();
        services.RemoveAll<CaddyManager>();
        services.AddSingleton<CaddyManager>(sp => ActivatorUtilities.CreateInstance<RecordingCaddyManager>(sp));
    };

    /// <summary>The proxy manager the host runs with, as the double that counts reloads.</summary>
    private static RecordingCaddyManager Caddy(AuthTestHost host) =>
        (RecordingCaddyManager)host.Services.GetRequiredService<CaddyManager>();

    private const string AuthHost = "watchtower.example.invalid";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // -- Create ----------------------------------------------------------------------------------

    [Fact]
    public async Task Create_StoresTheRealm_AndAudits() {
        using var host = AuthTestHost.Start(WithRealmsModule);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<CreateRealm.Command, CreateRealm.Response>(
                scope.ServiceProvider, new CreateRealm.Command("  Acme  ", "  acme  ", " Login.Acme.INVALID "));

            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal("Acme", result.Value.Realm.Name);
            Assert.Equal("acme", result.Value.Realm.Slug);
            // Hosts are lowercased because host names are case-insensitive and this one is compared against
            // an inbound Host header on every login.
            Assert.Equal("login.acme.invalid", result.Value.Realm.AuthHost);
            Assert.False(result.Value.Realm.IsSystem);
            Assert.Equal(0, result.Value.Realm.UserCount);
        }

        Assert.Equal([AuthEventKinds.RealmCreated], await host.AuditKindsAsync());
    }

    [Fact]
    public async Task Create_AcceptsARealmWithNoLoginHostYet() {
        using var host = AuthTestHost.Start(WithRealmsModule);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRealm.Command, CreateRealm.Response>(
            scope.ServiceProvider, new CreateRealm.Command("Acme", "acme"));

        // DNS usually is not ready when the realm is created; such a realm simply cannot be logged into
        // until it has a host, and its protected routes fail closed in the meantime.
        Assert.True(result.IsSuccess, Describe(result));
        Assert.Null(result.Value.Realm.AuthHost);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Acme")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("ac--me")]
    [InlineData("acme.corp")]
    [InlineData("acme_corp")]
    public async Task Create_RejectsASlugThatIsNotAStableIdentifier(string slug) {
        using var host = AuthTestHost.Start(WithRealmsModule);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRealm.Command, CreateRealm.Response>(
            scope.ServiceProvider, new CreateRealm.Command("Acme", slug));

        // The slug is immutable and travels in every assertion's `realm` claim, so it is constrained where
        // it enters rather than escaped wherever it is used — and never silently corrected, which would
        // make an administrator's records disagree with what their applications receive.
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Theory]
    [InlineData("https://login.acme.invalid")]
    [InlineData("login.acme.invalid:8443")]
    [InlineData("login.acme.invalid/login")]
    [InlineData("user@login.acme.invalid")]
    [InlineData("login.acme.invalid, evil.invalid")]
    [InlineData("login acme invalid")]
    public async Task Create_RejectsAnAuthHostThatIsNotABareHostName(string authHost) {
        using var host = AuthTestHost.Start(WithRealmsModule);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRealm.Command, CreateRealm.Response>(
            scope.ServiceProvider, new CreateRealm.Command("Acme", "acme", authHost));

        // The value decides which population a visitor authenticates into and becomes the `iss` of every
        // assertion the realm's apps receive; anything a parser would have to correct is refused rather
        // than accepted in its corrected form.
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task Create_RefusesTheWatchtowerLoginHost() {
        using var host = AuthTestHost.Start(WithRealmsModule, ("Watchtower:Auth:Host", AuthHost));

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRealm.Command, CreateRealm.Response>(
            scope.ServiceProvider, new CreateRealm.Command("Acme", "acme", AuthHost.ToUpperInvariant()));

        // One host cannot resolve to two populations: the one it resolved to would decide who can
        // administer the instance.
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task Create_RefusesADuplicateSlugOrLoginHost() {
        using var host = AuthTestHost.Start(WithRealmsModule);
        await CreateAsync(host, "acme", "login.acme.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var sameSlug = await SendAsync<CreateRealm.Command, CreateRealm.Response>(
            scope.ServiceProvider, new CreateRealm.Command("Acme again", "acme"));
        var sameHost = await SendAsync<CreateRealm.Command, CreateRealm.Response>(
            scope.ServiceProvider, new CreateRealm.Command("Contoso", "contoso", "login.acme.invalid"));

        Assert.Equal(ErrorKind.Conflict, sameSlug.Error.Kind);
        Assert.Equal(ErrorKind.Conflict, sameHost.Error.Kind);
    }

    [Fact]
    public async Task Create_NeverProducesASecondSystemRealm() {
        using var host = AuthTestHost.Start(WithRealmsModule);
        await CreateAsync(host, "acme", "login.acme.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        // There is no request field for it, and there must not be: "the operator population" has to stay
        // a single unambiguous row.
        Assert.Single(await db.Realms.Where(r => r.IsSystem).ToListAsync(Ct));
    }

    // -- Update ----------------------------------------------------------------------------------

    [Fact]
    public async Task Update_MovesTheLoginHost_AndSaysSoInTheTrail() {
        using var host = AuthTestHost.Start(WithRealmsModule);
        var id = await CreateAsync(host, "acme", "login.acme.invalid");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<UpdateRealm.Command, UpdateRealm.Response>(
                scope.ServiceProvider, new UpdateRealm.Command(id, "Acme Ltd", "sso.acme.invalid"));

            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal("Acme Ltd", result.Value.Realm.Name);
            Assert.Equal("sso.acme.invalid", result.Value.Realm.AuthHost);
            // Never editable: applications key off it.
            Assert.Equal("acme", result.Value.Realm.Slug);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var row = await db.AuditEvents.SingleAsync(e => e.Action == AuthEventKinds.RealmUpdated, Ct);
            // Moving the host orphans every session on the old one, so the old one is what an operator
            // reading the trail after "everyone was signed out" needs to see.
            Assert.Contains("authHost=login.acme.invalid->sso.acme.invalid", row.Detail);
        }
    }

    [Fact]
    public async Task Update_ClearsTheLoginHostWithAnEmptyString_AndLeavesItAloneWhenOmitted() {
        using var host = AuthTestHost.Start(WithRealmsModule);
        var id = await CreateAsync(host, "acme", "login.acme.invalid");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var renameOnly = await SendAsync<UpdateRealm.Command, UpdateRealm.Response>(
                scope.ServiceProvider, new UpdateRealm.Command(id, "Acme Ltd"));
            // Omission cannot mean "remove", or a client that predates the field would unset every realm
            // it saved.
            Assert.Equal("login.acme.invalid", renameOnly.Value.Realm.AuthHost);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var cleared = await SendAsync<UpdateRealm.Command, UpdateRealm.Response>(
                scope.ServiceProvider, new UpdateRealm.Command(id, AuthHost: ""));
            Assert.Null(cleared.Value.Realm.AuthHost);
        }
    }

    [Fact]
    public async Task Update_LetsTheOperatorRealmBeRenamed_ButNeverGivenALoginHost() {
        using var host = AuthTestHost.Start(WithRealmsModule, ("Watchtower:Auth:Host", AuthHost));

        await using var scope = host.Services.CreateAsyncScope();
        var renamed = await SendAsync<UpdateRealm.Command, UpdateRealm.Response>(
            scope.ServiceProvider, new UpdateRealm.Command(Realm.SystemRealmId, "Head Office"));
        var hosted = await SendAsync<UpdateRealm.Command, UpdateRealm.Response>(
            scope.ServiceProvider, new UpdateRealm.Command(Realm.SystemRealmId, AuthHost: "ops.example.invalid"));

        Assert.True(renamed.IsSuccess, Describe(renamed));
        Assert.Equal("Head Office", renamed.Value.Realm.Name);
        // Its login host is the configured Watchtower:Auth:Host; a row-level second answer would mean
        // authentication had to read the database to find its own login page.
        Assert.False(hosted.IsSuccess);
        Assert.Equal(ErrorKind.Validation, hosted.Error.Kind);
    }

    [Fact]
    public async Task Update_ReportsAnUnknownRealmAsNotFound() {
        using var host = AuthTestHost.Start(WithRealmsModule);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<UpdateRealm.Command, UpdateRealm.Response>(
            scope.ServiceProvider, new UpdateRealm.Command(404, "whatever"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    // -- Delete ----------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_RemovesAnEmptyRealm() {
        using var host = AuthTestHost.Start(WithRealmsModule);
        var id = await CreateAsync(host, "acme", "login.acme.invalid");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<DeleteRealm.Command, DeleteRealm.Response>(
                scope.ServiceProvider, new DeleteRealm.Command(id));
            Assert.True(result.IsSuccess, Describe(result));
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.Realms.AnyAsync(r => r.Id == id, Ct));
            var row = await db.AuditEvents.SingleAsync(e => e.Action == AuthEventKinds.RealmDeleted, Ct);
            Assert.Contains($"realm=acme#{id}", row.Detail);
        }
    }

    [Fact]
    public async Task Delete_RefusesTheOperatorRealm() {
        using var host = AuthTestHost.Start(WithRealmsModule);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<DeleteRealm.Command, DeleteRealm.Response>(
            scope.ServiceProvider, new DeleteRealm.Command(Realm.SystemRealmId));

        // It is the population every pre-realm row was backfilled into and the one every unrecognised host
        // resolves to; removing it would leave an instance nobody can administer.
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
    }

    [Fact]
    public async Task Delete_RefusesARealmThatStillHoldsAnything() {
        using var host = AuthTestHost.Start(WithRealmsModule);
        var withUser = await CreateAsync(host, "acme", "login.acme.invalid");
        var withGroup = await CreateAsync(host, "contoso", "login.contoso.invalid");
        var withTemplate = await CreateAsync(host, "initech", "login.initech.invalid");

        await host.AddUserAsync("carol", realmId: withUser);
        await host.AddGroupInRealmAsync("staff", withGroup);
        await host.AddRealmTemplateAsync("shop", withTemplate);

        await using var scope = host.Services.CreateAsyncScope();
        foreach (var id in new[] { withUser, withGroup, withTemplate }) {
            var result = await SendAsync<DeleteRealm.Command, DeleteRealm.Response>(
                scope.ServiceProvider, new DeleteRealm.Command(id));

            // No cascades, deliberately: deleting a population would otherwise take every account's
            // credentials and every category's tenant stacks with it in one call.
            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        }
    }

    // -- Proxy reconcile -------------------------------------------------------------------------

    /// <summary>
    /// Every realm write changes which hosts Caddy has to serve a login page on, so every one of them asks
    /// for a reload — the same post-commit, best-effort discipline the route CRUD and <c>proxy.setAccess</c>
    /// handlers use. Leaving it to the next unrelated reconcile would mean a new realm's visitors are
    /// redirected to a host nothing answers on, and — worse — that a protected route already on that domain
    /// stays gated, which is exactly the lockout the force-unprotected self-route exists to prevent.
    /// </summary>
    [Fact]
    public async Task EveryWrite_AsksTheProxyToReload() {
        using var host = AuthTestHost.Start(WithRealmsModule);
        var caddy = Caddy(host);

        var id = await CreateAsync(host, "acme", "login.acme.invalid");
        Assert.Equal(1, caddy.ApplyCount);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var updated = await SendAsync<UpdateRealm.Command, UpdateRealm.Response>(
                scope.ServiceProvider, new UpdateRealm.Command(id, AuthHost: "sso.acme.invalid"));
            Assert.True(updated.IsSuccess, Describe(updated));
        }
        Assert.Equal(2, caddy.ApplyCount);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var deleted = await SendAsync<DeleteRealm.Command, DeleteRealm.Response>(
                scope.ServiceProvider, new DeleteRealm.Command(id));
            Assert.True(deleted.IsSuccess, Describe(deleted));
        }
        Assert.Equal(3, caddy.ApplyCount);
    }

    [Fact]
    public async Task ARefusedWrite_DoesNotTouchTheProxy() {
        using var host = AuthTestHost.Start(WithRealmsModule);
        var caddy = Caddy(host);

        await using var scope = host.Services.CreateAsyncScope();
        var badSlug = await SendAsync<CreateRealm.Command, CreateRealm.Response>(
            scope.ServiceProvider, new CreateRealm.Command("Acme", "NOT A SLUG"));
        var unknown = await SendAsync<DeleteRealm.Command, DeleteRealm.Response>(
            scope.ServiceProvider, new DeleteRealm.Command(404));

        // Nothing committed, so nothing to serve differently — the reload rides the commit, not the call.
        Assert.False(badSlug.IsSuccess);
        Assert.False(unknown.IsSuccess);
        Assert.Equal(0, caddy.ApplyCount);
    }

    // -- List / authorization --------------------------------------------------------------------

    [Fact]
    public async Task List_ReportsWhatEachRealmHolds() {
        using var host = AuthTestHost.Start(WithRealmsModule);
        var acme = await CreateAsync(host, "acme", "login.acme.invalid");
        await host.AddUserAsync("carol", realmId: acme);
        await host.AddUserAsync("dave", realmId: acme);
        await host.AddGroupInRealmAsync("staff", acme);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListRealms.Query, ListRealms.Response>(
            scope.ServiceProvider, new ListRealms.Query());

        Assert.True(result.IsSuccess, Describe(result));
        // Ordered by slug: "acme" before "operator".
        Assert.Equal(["acme", Realm.SystemRealmSlug], result.Value.Realms.Select(r => r.Slug));
        var listed = result.Value.Realms[0];
        Assert.Equal(2, listed.UserCount);
        Assert.Equal(1, listed.GroupCount);
        Assert.Equal(0, listed.TemplateCount);
    }

    /// <summary>
    /// Creating a realm is creating a population — the most privileged act on this surface, because
    /// nothing else can grant access to accounts that do not exist yet.
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task Handlers_WithAuthEnabled_AreDeniedToANonAdministrator(string operation) {
        using var host = AuthTestHost.Start(WithRealmsModule, ("Watchtower:Auth:Enabled", "true"));

        await using (var scope = host.Services.CreateAsyncScope()) {
            var sp = scope.ServiceProvider;
            TestPrincipal.Seed(sp, isAdmin: false);

            var kind = operation switch {
                "list" => await DeniedKindAsync<ListRealms.Query, ListRealms.Response>(
                    sp, new ListRealms.Query()),
                "create" => await DeniedKindAsync<CreateRealm.Command, CreateRealm.Response>(
                    sp, new CreateRealm.Command("Intruder", "intruder")),
                "update" => await DeniedKindAsync<UpdateRealm.Command, UpdateRealm.Response>(
                    sp, new UpdateRealm.Command(1, "Intruder")),
                "delete" => await DeniedKindAsync<DeleteRealm.Command, DeleteRealm.Response>(
                    sp, new DeleteRealm.Command(1)),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
            };

            Assert.Equal(ErrorKind.Forbidden, kind);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            // Nothing ran: only the seeded operator realm exists, and it still has its own name.
            var realm = await db.Realms.SingleAsync(Ct);
            Assert.Equal(Realm.SystemRealmName, realm.Name);
        }
        Assert.Empty(await host.AuditKindsAsync());
    }

    // -- Helpers ---------------------------------------------------------------------------------

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>().HandleAsync(request, Ct);

    private static async ValueTask<ErrorKind> DeniedKindAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) {
        var result = await SendAsync<TRequest, TResponse>(scope, request);
        Assert.False(result.IsSuccess);
        return result.Error.Kind;
    }

    private static async Task<int> CreateAsync(AuthTestHost host, string slug, string? authHost = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateRealm.Command, CreateRealm.Response>(
            scope.ServiceProvider, new CreateRealm.Command(slug, slug, authHost));
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value.Realm.Id;
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
