using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Credentials.Handlers;
using Watchtower.Application.Modules.Groups.Handlers;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Modules.Tenancy.Handlers;
using Watchtower.Application.Modules.Users.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The realm parameters and realm-consistency rules the management surface grew (docs/central-auth/design.md
/// §13) — where an account, a group or a category is placed, which grants may name which subjects, and the
/// central rule that the surface itself belongs to the operator population.
/// </summary>
public sealed class RealmScopedSurfaceTests {
    private static readonly Action<IServiceCollection> WithHandlers = services => {
        services.AddCreateUser();
        services.AddUpdateUser();
        services.AddListUsers();
        services.AddCreateGroup();
        services.AddListGroups();
        services.AddSetGroupMembers();
        services.AddCreateTemplate();
        services.AddUpdateTemplate();
        services.AddSetAccess();
        services.AddListCredentials();
    };

    private const string Password = "correct-horse-battery";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // -- users.* ---------------------------------------------------------------------------------

    [Fact]
    public async Task CreateUser_PlacesTheAccountInTheRequestedRealm_AndDefaultsToTheOperatorOne() {
        using var host = AuthTestHost.Start(WithHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var inRealm = await SendAsync<CreateUser.Command, CreateUser.Response>(
            scope.ServiceProvider, new CreateUser.Command("carol", Password, null, false, acme));
        var defaulted = await SendAsync<CreateUser.Command, CreateUser.Response>(
            scope.ServiceProvider, new CreateUser.Command("alice", Password, null, false));

        Assert.True(inRealm.IsSuccess, Describe(inRealm));
        Assert.Equal(acme, inRealm.Value.User.RealmId);
        // A client that predates realms omits the field and keeps creating operator accounts.
        Assert.True(defaulted.IsSuccess, Describe(defaulted));
        Assert.Equal(Realm.SystemRealmId, defaulted.Value.User.RealmId);
    }

    [Fact]
    public async Task CreateUser_AllowsTheSameNameInTwoRealms_ButNotTwiceInOne() {
        using var host = AuthTestHost.Start(WithHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var first = await SendAsync<CreateUser.Command, CreateUser.Response>(
            scope.ServiceProvider, new CreateUser.Command("support", Password, null, false));
        var elsewhere = await SendAsync<CreateUser.Command, CreateUser.Response>(
            scope.ServiceProvider, new CreateUser.Command("support", Password, null, false, acme));
        // Identity's duplicate check has to be answered about the realm the account is going into, which
        // is what pinning the realm context before UserManager runs is for.
        var again = await SendAsync<CreateUser.Command, CreateUser.Response>(
            scope.ServiceProvider, new CreateUser.Command("SUPPORT", Password, null, false, acme));

        Assert.True(first.IsSuccess, Describe(first));
        Assert.True(elsewhere.IsSuccess, Describe(elsewhere));
        Assert.False(again.IsSuccess);
        Assert.Equal(ErrorKind.Validation, again.Error.Kind);
    }

    [Fact]
    public async Task CreateUser_RefusesTheAdminRoleOutsideTheOperatorRealm() {
        using var host = AuthTestHost.Start(WithHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateUser.Command, CreateUser.Response>(
            scope.ServiceProvider, new CreateUser.Command("carol", Password, null, IsAdmin: true, acme));

        // The role administers the whole instance, which is precisely what a realm exists to keep a
        // customer population away from.
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task CreateUser_RefusesARealmThatDoesNotExist() {
        using var host = AuthTestHost.Start(WithHandlers);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateUser.Command, CreateUser.Response>(
            scope.ServiceProvider, new CreateUser.Command("carol", Password, null, false, 404));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task UpdateUser_RefusesTheAdminRoleOutsideTheOperatorRealm_AndRenamesWithinTheRightRealm() {
        using var host = AuthTestHost.Start(WithHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var carol = await host.AddUserAsync("carol", realmId: acme);
        // An operator account whose name the rename below would collide with, if the duplicate check were
        // answered about the wrong population.
        await host.AddUserAsync("support");

        await using var scope = host.Services.CreateAsyncScope();
        var promoted = await SendAsync<UpdateUser.Command, UpdateUser.Response>(
            scope.ServiceProvider, new UpdateUser.Command(carol, "carol", null, IsAdmin: true));
        var renamed = await SendAsync<UpdateUser.Command, UpdateUser.Response>(
            scope.ServiceProvider, new UpdateUser.Command(carol, "support", null, IsAdmin: false));

        Assert.False(promoted.IsSuccess);
        Assert.Equal(ErrorKind.Validation, promoted.Error.Kind);
        Assert.True(renamed.IsSuccess, Describe(renamed));
        Assert.Equal(acme, renamed.Value.User.RealmId);
    }

    [Fact]
    public async Task ListUsers_FiltersByRealm_AndOtherwiseShowsTheWholeEstate() {
        using var host = AuthTestHost.Start(WithHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        await host.AddUserAsync("alice");
        await host.AddUserAsync("carol", realmId: acme);

        await using var scope = host.Services.CreateAsyncScope();
        var all = await SendAsync<ListUsers.Query, ListUsers.Response>(
            scope.ServiceProvider, new ListUsers.Query());
        var filtered = await SendAsync<ListUsers.Query, ListUsers.Response>(
            scope.ServiceProvider, new ListUsers.Query(acme));

        Assert.Equal(["alice", "carol"], all.Value.Users.Select(u => u.UserName));
        Assert.Equal(["carol"], filtered.Value.Users.Select(u => u.UserName));
    }

    // -- groups.* --------------------------------------------------------------------------------

    [Fact]
    public async Task CreateGroup_PlacesTheGroup_AndAllowsTheSameNameInAnotherRealm() {
        using var host = AuthTestHost.Start(WithHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var operatorStaff = await SendAsync<CreateGroup.Command, CreateGroup.Response>(
            scope.ServiceProvider, new CreateGroup.Command("staff"));
        var realmStaff = await SendAsync<CreateGroup.Command, CreateGroup.Response>(
            scope.ServiceProvider, new CreateGroup.Command("Staff", acme));
        var duplicate = await SendAsync<CreateGroup.Command, CreateGroup.Response>(
            scope.ServiceProvider, new CreateGroup.Command("STAFF", acme));

        Assert.Equal(Realm.SystemRealmId, operatorStaff.Value.Group.RealmId);
        Assert.Equal(acme, realmStaff.Value.Group.RealmId);
        Assert.False(duplicate.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, duplicate.Error.Kind);
    }

    [Fact]
    public async Task ListGroups_FiltersByRealm() {
        using var host = AuthTestHost.Start(WithHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        await host.AddGroupAsync("operators");
        await host.AddGroupInRealmAsync("customers", acme);

        await using var scope = host.Services.CreateAsyncScope();
        var all = await SendAsync<ListGroups.Query, ListGroups.Response>(
            scope.ServiceProvider, new ListGroups.Query());
        var filtered = await SendAsync<ListGroups.Query, ListGroups.Response>(
            scope.ServiceProvider, new ListGroups.Query(acme));

        Assert.Equal(["customers", "operators"], all.Value.Groups.Select(g => g.Name));
        Assert.Equal(["customers"], filtered.Value.Groups.Select(g => g.Name));
    }

    [Fact]
    public async Task SetGroupMembers_RefusesAnAccountFromAnotherRealm() {
        using var host = AuthTestHost.Start(WithHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var group = await host.AddGroupInRealmAsync("customers", acme);
        var carol = await host.AddUserAsync("carol", realmId: acme);
        var alice = await host.AddUserAsync("alice");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var mixed = await SendAsync<SetGroupMembers.Command, SetGroupMembers.Response>(
                scope.ServiceProvider, new SetGroupMembers.Command(group, [carol, alice]));

            // A membership that could never take effect is an administrator's mistake worth naming: the
            // roster would otherwise show an account as having access the access check refuses it.
            Assert.False(mixed.IsSuccess);
            Assert.Equal(ErrorKind.Validation, mixed.Error.Kind);
            Assert.Contains(alice.ToString(System.Globalization.CultureInfo.InvariantCulture), mixed.Error.Message);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            // Refused whole: the good half was not applied either.
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.GroupMembers.AnyAsync(m => m.GroupId == group, Ct));

            var ownRealm = await SendAsync<SetGroupMembers.Command, SetGroupMembers.Response>(
                scope.ServiceProvider, new SetGroupMembers.Command(group, [carol]));
            Assert.True(ownRealm.IsSuccess, Describe(ownRealm));
        }
    }

    // -- templates.* -----------------------------------------------------------------------------

    [Fact]
    public async Task CreateTemplate_PlacesTheCategory_AndDefaultsToTheOperatorRealm() {
        using var host = AuthTestHost.Start(WithHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");

        await using var scope = host.Services.CreateAsyncScope();
        var inRealm = await SendAsync<CreateTemplate.Command, CreateTemplate.Response>(
            scope.ServiceProvider, NewTemplate("shop", acme));
        var defaulted = await SendAsync<CreateTemplate.Command, CreateTemplate.Response>(
            scope.ServiceProvider, NewTemplate("tools"));

        Assert.Equal(acme, inRealm.Value.Template.RealmId);
        Assert.Equal(Realm.SystemRealmId, defaulted.Value.Template.RealmId);
    }

    [Fact]
    public async Task UpdateTemplate_MovesAnEmptyCategory_AndRefusesToMoveAPopulatedOne() {
        using var host = AuthTestHost.Start(WithHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var empty = await host.AddRealmTemplateAsync("shop");
        var populated = await host.AddRealmTemplateAsync("tools");
        await host.AddRouteAsync("one.tools.example.invalid", AccessMode.Authenticated, populated);

        await using var scope = host.Services.CreateAsyncScope();
        var moved = await SendAsync<UpdateTemplate.Command, UpdateTemplate.Response>(
            scope.ServiceProvider, EditTemplate(empty, "shop", acme));
        var refused = await SendAsync<UpdateTemplate.Command, UpdateTemplate.Response>(
            scope.ServiceProvider, EditTemplate(populated, "tools", acme));

        Assert.True(moved.IsSuccess, Describe(moved));
        Assert.Equal(acme, moved.Value.Template.RealmId);
        // Moving a populated category would re-point every tenant route at another population: the
        // accounts using them would stop being admitted, and the new realm's would be let in.
        Assert.False(refused.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, refused.Error.Kind);
    }

    [Fact]
    public async Task UpdateTemplate_LeavesTheRealmAloneWhenOmitted() {
        using var host = AuthTestHost.Start(WithHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var template = await host.AddRealmTemplateAsync("shop", acme);
        await host.AddRouteAsync("one.shop.example.invalid", AccessMode.Authenticated, template);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<UpdateTemplate.Command, UpdateTemplate.Response>(
            scope.ServiceProvider,
            EditTemplate(template, "shop-renamed", realmId: null,
                repositoryUrl: "https://example.invalid/shop.git"));

        // A populated category is still editable — it is only the realm that is pinned.
        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(acme, result.Value.Template.RealmId);
        Assert.Equal("shop-renamed", result.Value.Template.Name);
    }

    // -- proxy.setAccess -------------------------------------------------------------------------

    [Fact]
    public async Task SetAccess_RefusesAGrantForASubjectOfAnotherRealm() {
        using var host = AuthTestHost.Start(WithHandlers);
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var template = await host.AddRealmTemplateAsync("shop", acme);
        var route = await host.AddRouteAsync("one.shop.example.invalid", AccessMode.Restricted, template);

        var carol = await host.AddUserAsync("carol", realmId: acme);
        var alice = await host.AddUserAsync("alice");
        var operators = await host.AddGroupAsync("operators", alice);

        await using var scope = host.Services.CreateAsyncScope();
        var foreignUser = await SendAsync<SetAccess.Command, SetAccess.Response>(
            scope.ServiceProvider,
            new SetAccess.Command(route.Id, AccessMode.Restricted, null, [alice]));
        var foreignGroup = await SendAsync<SetAccess.Command, SetAccess.Response>(
            scope.ServiceProvider,
            new SetAccess.Command(route.Id, AccessMode.Restricted, null, [], GrantedGroupIds: [operators]));
        var ownRealm = await SendAsync<SetAccess.Command, SetAccess.Response>(
            scope.ServiceProvider,
            new SetAccess.Command(route.Id, AccessMode.Restricted, null, [carol]));

        // Refused at write time as well as ignored at access time: a stored grant that admits nobody reads
        // like access somebody has.
        Assert.False(foreignUser.IsSuccess);
        Assert.Equal(ErrorKind.Validation, foreignUser.Error.Kind);
        Assert.False(foreignGroup.IsSuccess);
        Assert.Equal(ErrorKind.Validation, foreignGroup.Error.Kind);
        Assert.True(ownRealm.IsSuccess, Describe(ownRealm));
        Assert.Equal([carol], ownRealm.Value.GrantedUserIds);
    }

    // -- The management surface is the operator population's (D10) -------------------------------

    /// <summary>
    /// A realm account's session is perfectly valid — it signs in, it holds a cookie, it passes the
    /// forward-auth surface for its own applications. What it cannot do is anything on the management API,
    /// and the refusal is central rather than declared per handler, so it applies to a handler that
    /// requires the Admin role and to one that only inherits the assembly default alike.
    /// </summary>
    [Fact]
    public async Task ARealmPrincipal_IsForbiddenOnEveryHandler_EvenHoldingTheAdminRole() {
        using var host = AuthTestHost.Start(WithHandlers, ("Watchtower:Auth:Enabled", "true"));

        await using var scope = host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        // Deliberately carrying the Admin role: what refuses this call has to be the realm and not the
        // role, or the rule would be satisfiable by a claim a realm account could somehow acquire.
        TestPrincipal.Seed(sp, TestPrincipal.New(isAdmin: true, realmSlug: "acme"));

        var roleGated = await SendAsync<ListGroups.Query, ListGroups.Response>(sp, new ListGroups.Query());
        var defaultGated = await SendAsync<ListCredentials.Query, ListCredentials.Response>(
            sp, new ListCredentials.Query());

        Assert.Equal(ErrorKind.Forbidden, roleGated.Error.Kind);
        Assert.Equal(ErrorKind.Forbidden, defaultGated.Error.Kind);
    }

    [Fact]
    public async Task AnOperatorPrincipal_IsUnaffected() {
        using var host = AuthTestHost.Start(WithHandlers, ("Watchtower:Auth:Enabled", "true"));

        await using var scope = host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        TestPrincipal.Seed(sp, TestPrincipal.New(isAdmin: true));

        var result = await SendAsync<ListGroups.Query, ListGroups.Response>(sp, new ListGroups.Query());

        Assert.True(result.IsSuccess, Describe(result));
    }

    [Fact]
    public async Task APrincipalStatingNoRealmAtAll_IsForbidden() {
        using var host = AuthTestHost.Start(WithHandlers, ("Watchtower:Auth:Enabled", "true"));

        await using var scope = host.Services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        TestPrincipal.Seed(sp, new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity([
                new System.Security.Claims.Claim(WatchtowerClaims.UserId, "7"),
                new System.Security.Claims.Claim(WatchtowerClaims.Role, WatchtowerClaims.AdminRole),
            ], "WatchtowerSession", WatchtowerClaims.Name, WatchtowerClaims.Role)));

        // Fail-closed: a future authentication path that forgets to state the realm loses the management
        // surface rather than being handed it.
        var result = await SendAsync<ListGroups.Query, ListGroups.Response>(sp, new ListGroups.Query());

        Assert.Equal(ErrorKind.Forbidden, result.Error.Kind);
    }

    [Fact]
    public async Task WithAuthDisabled_TheLocalOperatorStillReachesEverything() {
        using var host = AuthTestHost.Start(WithHandlers);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListGroups.Query, ListGroups.Response>(
            scope.ServiceProvider, new ListGroups.Query());

        // ImplicitAdminCurrentUser reports the operator realm, so Auth:Enabled=false behaves exactly as it
        // did before realms existed.
        Assert.True(result.IsSuccess, Describe(result));
    }

    // -- Helpers ---------------------------------------------------------------------------------

    private static CreateTemplate.Command NewTemplate(string name, int? realmId = null) => new(
        name,
        $"https://example.invalid/{name}.git",
        "docker-compose.yml",
        "main",
        null,
        $"{{tenant}}.{name}.example.invalid",
        "web",
        8080,
        null,
        realmId);

    /// <param name="repositoryUrl">
    /// What the form posts back for the (now product-owned) source. Defaults to the URL
    /// <see cref="AccessTestEstate.AddRealmTemplateAsync"/> derives from the template's <em>original</em>
    /// name — since ADR-0026 templates.update refuses a repository URL that actually changed, so a
    /// rename has to keep posting the source it was loaded with.
    /// </param>
    private static UpdateTemplate.Command EditTemplate(
        int id, string name, int? realmId, string? repositoryUrl = null) => new(
        id,
        name,
        repositoryUrl ?? $"https://example.invalid/{name}.git",
        "docker-compose.yml",
        "main",
        null,
        $"{{tenant}}.{name}.example.invalid",
        "web",
        8080,
        null,
        realmId);

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>().HandleAsync(request, Ct);

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
