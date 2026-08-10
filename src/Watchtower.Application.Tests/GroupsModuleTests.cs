using System.Security.Claims;
using Elarion.Abstractions;
using Elarion.Abstractions.Dispatch;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Groups;
using Watchtower.Application.Modules.Groups.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the Groups module through its real generated handler pipelines, so the
/// <c>[RequireRole("Admin")]</c> decorator and the module's own rules are exercised together: the name
/// charset that keeps a group name from meaning two things once it is forwarded, the whole-set membership
/// reconciliation, the cascades a delete relies on, and the audit trail.
/// </summary>
public sealed class GroupsModuleTests {
    private const string Password = "correct-horse-battery";

    /// <summary>Every Groups handler, added the way the generated module registration does.</summary>
    private static readonly Action<IServiceCollection> WithGroupsModule = services => {
        services.AddListGroups();
        services.AddCreateGroup();
        services.AddRenameGroup();
        services.AddDeleteGroup();
        services.AddGetGroupMembers();
        services.AddSetGroupMembers();
    };

    // -- Create / list ---------------------------------------------------------------------------

    [Fact]
    public async Task Create_TrimsTheName_ReportsAnEmptyGroup_AndAudits() {
        using var host = AuthTestHost.Start(WithGroupsModule);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<CreateGroup.Command, CreateGroup.Response>(
                scope.ServiceProvider, new CreateGroup.Command("  Platform Team  "));

            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal("Platform Team", result.Value.Group.Name);
            Assert.Equal(0, result.Value.Group.MemberCount);
        }

        var listed = Assert.Single(await ListAsync(host));
        Assert.Equal("Platform Team", listed.Name);
        Assert.Equal(0, listed.MemberCount);
        Assert.Equal([AuthEventKinds.GroupCreated], await host.AuditKindsAsync());
    }

    [Fact]
    public async Task Create_RejectsANameThatOnlyDiffersInCase() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        await CreateAsync(host, "viewers");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateGroup.Command, CreateGroup.Response>(
            scope.ServiceProvider, new CreateGroup.Command("VIEWERS"));

        // Uniqueness lives on the normalized column, exactly as it does for user names — otherwise two
        // groups could forward the same name to an upstream and mean different sets of people.
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Single(await ListAsync(host));
    }

    /// <summary>
    /// The charset rules exist because a group name is not an internal label: it is forwarded verbatim in
    /// the comma-joined group header and the JWT's <c>groups</c> claim. A comma would be read downstream as
    /// two memberships; a control character is header-injection material; a non-ASCII name has no agreed
    /// header encoding. All of them are refused where the name enters, so no forwarding site has to cope.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("viewers,admins")]
    [InlineData("view\ners")]
    [InlineData("viewers\u007F")]
    [InlineData("Überwacher")]
    public async Task Create_RejectsANameThatCouldNotBeForwardedFaithfully(string name) {
        using var host = AuthTestHost.Start(WithGroupsModule);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<CreateGroup.Command, CreateGroup.Response>(
                scope.ServiceProvider, new CreateGroup.Command(name));

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        }

        Assert.Empty(await ListAsync(host));
        Assert.Empty(await host.AuditKindsAsync());
    }

    [Fact]
    public async Task Create_RejectsANameLongerThanTheLimit_AndAcceptsOneExactlyAtIt() {
        using var host = AuthTestHost.Start(WithGroupsModule);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var tooLong = await SendAsync<CreateGroup.Command, CreateGroup.Response>(
                scope.ServiceProvider,
                new CreateGroup.Command(new string('g', GroupMapping.MaxNameLength + 1)));
            Assert.False(tooLong.IsSuccess);
            Assert.Equal(ErrorKind.Validation, tooLong.Error.Kind);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var atLimit = await SendAsync<CreateGroup.Command, CreateGroup.Response>(
                scope.ServiceProvider, new CreateGroup.Command(new string('g', GroupMapping.MaxNameLength)));
            Assert.True(atLimit.IsSuccess, Describe(atLimit));
        }

        Assert.Single(await ListAsync(host));
    }

    [Fact]
    public async Task List_ReportsTheMemberCount_IncludingZero() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        var staff = await CreateAsync(host, "staff");
        await CreateAsync(host, "empty");
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");
        await SetMembersAsync(host, staff, [alice, bob]);

        var groups = await ListAsync(host);

        // Ordered by name, and a group with no members is still listed — it is a group waiting to be filled.
        Assert.Equal(["empty", "staff"], groups.Select(g => g.Name));
        Assert.Equal([0, 2], groups.Select(g => g.MemberCount));
    }

    // -- Rename ----------------------------------------------------------------------------------

    [Fact]
    public async Task Rename_KeepsMembership_AndRecordsThePreviousName() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        var id = await CreateAsync(host, "staff");
        var alice = await host.AddUserAsync("alice");
        await SetMembersAsync(host, id, [alice]);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<RenameGroup.Command, RenameGroup.Response>(
                scope.ServiceProvider, new RenameGroup.Command(id, "employees"));

            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal("employees", result.Value.Group.Name);
            // Membership is by id, so a rename moves nobody in or out.
            Assert.Equal(1, result.Value.Group.MemberCount);
        }

        Assert.Equal([alice], await MembersAsync(host, id));

        // The old name is what upstreams were being told until now; a row naming only the new one would
        // not explain why an application stopped recognising a group.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var renamed = await db.AuthEvents.SingleAsync(e => e.Kind == AuthEventKinds.GroupRenamed, Ct);
            Assert.Contains("from=staff", renamed.Detail);
            Assert.Contains($"group=employees#{id}", renamed.Detail);
        }
    }

    [Fact]
    public async Task Rename_AllowsRecasingItsOwnName() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        var id = await CreateAsync(host, "staff");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<RenameGroup.Command, RenameGroup.Response>(
            scope.ServiceProvider, new RenameGroup.Command(id, "Staff"));

        // The duplicate check excludes the group's own row, so a group does not collide with itself.
        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("Staff", result.Value.Group.Name);
    }

    [Fact]
    public async Task Rename_RejectsANameAnotherGroupAlreadyHas() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        var id = await CreateAsync(host, "staff");
        await CreateAsync(host, "viewers");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<RenameGroup.Command, RenameGroup.Response>(
                scope.ServiceProvider, new RenameGroup.Command(id, "Viewers"));

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        }

        Assert.Equal(["staff", "viewers"], (await ListAsync(host)).Select(g => g.Name));
    }

    [Fact]
    public async Task Rename_ReportsAnUnknownGroupAsNotFound() {
        using var host = AuthTestHost.Start(WithGroupsModule);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<RenameGroup.Command, RenameGroup.Response>(
            scope.ServiceProvider, new RenameGroup.Command(404, "whatever"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    [Fact]
    public async Task Rename_ValidatesTheNameBeforeLookingTheGroupUp() {
        using var host = AuthTestHost.Start(WithGroupsModule);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<RenameGroup.Command, RenameGroup.Response>(
            scope.ServiceProvider, new RenameGroup.Command(404, "bad,name"));

        // A name that could never be stored is a bad request whatever it is aimed at — and answering
        // Validation rather than NotFound keeps the two failures from being confused in a form.
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    // -- Members ---------------------------------------------------------------------------------

    [Fact]
    public async Task SetMembers_ReconcilesTheSet_WithoutDuplicatingRows() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        var id = await CreateAsync(host, "staff");
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");
        var carol = await host.AddUserAsync("carol");

        // Save the same set twice, then shift it: alice stays, bob leaves, carol joins.
        await SetMembersAsync(host, id, [alice, bob]);
        await SetMembersAsync(host, id, [alice, bob]);
        await SetMembersAsync(host, id, [alice, carol]);

        Assert.Equal([alice, carol], await MembersAsync(host, id));

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        // Exactly one row per current member — no duplicate for the member present across every save.
        Assert.Equal(2, await db.GroupMembers.CountAsync(m => m.GroupId == id, Ct));
    }

    [Fact]
    public async Task SetMembers_RecordsWhatChanged_NotJustTheResultingSet() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        var id = await CreateAsync(host, "staff");
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");

        await SetMembersAsync(host, id, [alice, bob]);
        await SetMembersAsync(host, id, [alice]);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var rows = await db.AuthEvents
            .Where(e => e.Kind == AuthEventKinds.GroupMembersChanged)
            .OrderBy(e => e.Id)
            .ToListAsync(Ct);

        Assert.Equal(2, rows.Count);
        Assert.Contains("added=2; removed=0", rows[0].Detail);
        // The row an operator reads after an unexpected denial: who stopped being a member, and when.
        Assert.Contains("added=0; removed=1", rows[1].Detail);
    }

    [Fact]
    public async Task SetMembers_WithAnUnknownUser_ChangesNothingAtAll() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        var id = await CreateAsync(host, "staff");
        var alice = await host.AddUserAsync("alice");
        await SetMembersAsync(host, id, [alice]);
        var bob = await host.AddUserAsync("bob");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetGroupMembers.Command, SetGroupMembers.Response>(
                scope.ServiceProvider, new SetGroupMembers.Command(id, [bob, 4040]));

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorKind.Validation, result.Error.Kind);
            Assert.Contains("4040", result.Error.Message);
        }

        // Validated before any write, so the good half of the set was not applied either: alice is still
        // in and bob is still out, and the refused command left no trace beyond the one earlier save.
        Assert.Equal([alice], await MembersAsync(host, id));
        Assert.Single(await host.AuditKindsAsync(), k => k == AuthEventKinds.GroupMembersChanged);
    }

    [Fact]
    public async Task SetMembers_DeduplicatesRepeatedIds() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        var id = await CreateAsync(host, "staff");
        var alice = await host.AddUserAsync("alice");

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<SetGroupMembers.Command, SetGroupMembers.Response>(
                scope.ServiceProvider, new SetGroupMembers.Command(id, [alice, alice]));

            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal([alice], result.Value.UserIds);
        }

        // A repeated id must not become a second row — the unique index would refuse it anyway, but the
        // caller gets a saved membership rather than a database error.
        Assert.Equal([alice], await MembersAsync(host, id));
    }

    [Fact]
    public async Task SetMembers_CanEmptyAGroup() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        var id = await CreateAsync(host, "staff");
        var alice = await host.AddUserAsync("alice");
        await SetMembersAsync(host, id, [alice]);

        await SetMembersAsync(host, id, []);

        Assert.Empty(await MembersAsync(host, id));
    }

    [Fact]
    public async Task GetMembers_TellsAnUnknownGroupApartFromAnEmptyOne() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        var id = await CreateAsync(host, "staff");

        await using var scope = host.Services.CreateAsyncScope();
        var empty = await SendAsync<GetGroupMembers.Query, GetGroupMembers.Response>(
            scope.ServiceProvider, new GetGroupMembers.Query(id));
        Assert.True(empty.IsSuccess, Describe(empty));
        Assert.Empty(empty.Value.UserIds);

        var unknown = await SendAsync<GetGroupMembers.Query, GetGroupMembers.Response>(
            scope.ServiceProvider, new GetGroupMembers.Query(404));
        Assert.False(unknown.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, unknown.Error.Kind);
    }

    [Fact]
    public async Task SetMembers_ReportsAnUnknownGroupAsNotFound() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        var alice = await host.AddUserAsync("alice");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<SetGroupMembers.Command, SetGroupMembers.Response>(
            scope.ServiceProvider, new SetGroupMembers.Command(404, [alice]));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    // -- Delete ----------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_CascadesMembershipsAndRouteGrants_AndKeepsTheAuditRow() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        var id = await CreateAsync(host, "staff");
        var alice = await host.AddUserAsync("alice");
        await SetMembersAsync(host, id, [alice]);
        var routeId = await SeedRestrictedRouteIdAsync(host);
        await host.GrantGroupAsync(routeId, id);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var result = await SendAsync<DeleteGroup.Command, DeleteGroup.Response>(
                scope.ServiceProvider, new DeleteGroup.Command(id));
            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal(id, result.Value.Id);
        }

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            // Nothing revoked these by hand — the FK cascades did, which is what the handler relies on.
            Assert.False(await db.GroupMembers.AnyAsync(m => m.GroupId == id, Ct));
            Assert.False(await db.RouteAccessGrants.AnyAsync(g => g.GroupId == id, Ct));
            // The account itself is untouched: a group is a grouping, not an owner.
            Assert.True(await db.Users.AnyAsync(u => u.Id == alice, Ct));

            // The trail outlives its subject, so the name and the scale of the revocation live in Detail.
            var deleted = await db.AuthEvents.SingleAsync(e => e.Kind == AuthEventKinds.GroupDeleted, Ct);
            Assert.Contains($"group=staff#{id}", deleted.Detail);
            Assert.Contains("members=1; grantsCascaded=1", deleted.Detail);
        }
    }

    [Fact]
    public async Task Delete_ReportsAnUnknownGroupAsNotFound() {
        using var host = AuthTestHost.Start(WithGroupsModule);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<DeleteGroup.Command, DeleteGroup.Response>(
            scope.ServiceProvider, new DeleteGroup.Command(404));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    [Fact]
    public async Task DeletingAUser_LeavesTheGroupAndDropsOnlyThatMembership() {
        using var host = AuthTestHost.Start(WithGroupsModule);
        var id = await CreateAsync(host, "staff");
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");
        await SetMembersAsync(host, id, [alice, bob]);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = await users.FindByIdAsync(bob.ToString());
            Assert.NotNull(user);
            Assert.True((await users.DeleteAsync(user)).Succeeded);
        }

        // The other end of the same cascade: deleting an account removes it from every group it was in
        // without disturbing the group or its other members.
        Assert.Equal([alice], await MembersAsync(host, id));
        Assert.Equal(1, (await ListAsync(host)).Single().MemberCount);
    }

    // -- Authorization ---------------------------------------------------------------------------

    /// <summary>
    /// Every handler here is <c>[RequireRole("Admin")]</c> — adding an account to a group grants it every
    /// route that group is named on, so a signed-in non-administrator is refused by the generated decorator
    /// before the handler runs at all (which is why an id that does not exist still yields Forbidden).
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("create")]
    [InlineData("rename")]
    [InlineData("delete")]
    [InlineData("getMembers")]
    [InlineData("setMembers")]
    public async Task Handlers_WithAuthEnabled_AreDeniedToANonAdministrator(string operation) {
        using var host = AuthTestHost.Start(WithGroupsModule, ("Watchtower:Auth:Enabled", "true"));

        await using (var scope = host.Services.CreateAsyncScope()) {
            var sp = scope.ServiceProvider;
            SeedPrincipal(sp, isAdmin: false);

            var kind = operation switch {
                "list" => await DeniedKindAsync<ListGroups.Query, ListGroups.Response>(
                    sp, new ListGroups.Query()),
                "create" => await DeniedKindAsync<CreateGroup.Command, CreateGroup.Response>(
                    sp, new CreateGroup.Command("intruders")),
                "rename" => await DeniedKindAsync<RenameGroup.Command, RenameGroup.Response>(
                    sp, new RenameGroup.Command(1, "intruders")),
                "delete" => await DeniedKindAsync<DeleteGroup.Command, DeleteGroup.Response>(
                    sp, new DeleteGroup.Command(1)),
                "getMembers" => await DeniedKindAsync<GetGroupMembers.Query, GetGroupMembers.Response>(
                    sp, new GetGroupMembers.Query(1)),
                "setMembers" => await DeniedKindAsync<SetGroupMembers.Command, SetGroupMembers.Response>(
                    sp, new SetGroupMembers.Command(1, [1])),
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
            };

            Assert.Equal(ErrorKind.Forbidden, kind);
        }

        // Nothing ran: no group was created and the trail records no administrative action.
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            Assert.False(await db.Groups.AnyAsync(Ct));
        }
        Assert.Empty(await host.AuditKindsAsync());
    }

    // -- Helpers ---------------------------------------------------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>().HandleAsync(request, Ct);

    private static async ValueTask<ErrorKind> DeniedKindAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) {
        var result = await SendAsync<TRequest, TResponse>(scope, request);
        Assert.False(result.IsSuccess);
        return result.Error.Kind;
    }

    private static async Task<int> CreateAsync(AuthTestHost host, string name) {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CreateGroup.Command, CreateGroup.Response>(
            scope.ServiceProvider, new CreateGroup.Command(name));
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value.Group.Id;
    }

    private static async Task SetMembersAsync(AuthTestHost host, int groupId, IReadOnlyList<int> userIds) {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<SetGroupMembers.Command, SetGroupMembers.Response>(
            scope.ServiceProvider, new SetGroupMembers.Command(groupId, userIds));
        Assert.True(result.IsSuccess, Describe(result));
    }

    private static async Task<IReadOnlyList<int>> MembersAsync(AuthTestHost host, int groupId) {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<GetGroupMembers.Query, GetGroupMembers.Response>(
            scope.ServiceProvider, new GetGroupMembers.Query(groupId));
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value.UserIds;
    }

    private static async Task<IReadOnlyList<GroupDto>> ListAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListGroups.Query, ListGroups.Response>(
            scope.ServiceProvider, new ListGroups.Query());
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value.Groups;
    }

    /// <summary>Seeds a stack and a Restricted route on it, returning the route id.</summary>
    private static async Task<int> SeedRestrictedRouteIdAsync(AuthTestHost host) =>
        (await host.AddRouteAsync("demo.example.invalid", AccessMode.Restricted)).Id;

    /// <summary>Applies a principal the way every Elarion transport does — through the dispatch-scope rail.</summary>
    private static void SeedPrincipal(IServiceProvider scope, bool isAdmin) {
        var claims = new List<Claim> {
            new(WatchtowerClaims.UserId, "7"),
            new(WatchtowerClaims.Name, "caller"),
        };
        if (isAdmin) claims.Add(new Claim(WatchtowerClaims.Role, WatchtowerClaims.AdminRole));

        var context = new DispatchScopeContext();
        context.Set(new ClaimsPrincipal(new ClaimsIdentity(
            claims, "WatchtowerSession", WatchtowerClaims.Name, WatchtowerClaims.Role)));
        scope.SeedScope(context);
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
