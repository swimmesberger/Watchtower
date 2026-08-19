using Elarion.Abstractions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Audit;
using Watchtower.Application.Modules.Audit.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the Audit module — the read-only view over the trail every other module writes
/// (docs/central-auth/README.md, "No audit-viewing UI") — through its real generated handler pipelines,
/// so the <c>[RequireRole("Admin")]</c> decorator is exercised alongside the paging arithmetic, the
/// filters and the projection of a row whose subject has since been deleted.
/// </summary>
public sealed class AuditModuleTests {
    /// <summary>Both Audit handlers, added the way the generated module registration does.</summary>
    private static readonly Action<IServiceCollection> WithAuditModule = services => {
        services.AddListAuthEvents();
        services.AddListAuthEventKinds();
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // -- Authorization ---------------------------------------------------------------------------

    /// <summary>
    /// The trail names accounts and apps across every realm, so reading it is an instance-administration
    /// act. Both handlers carry <c>[RequireRole("Admin")]</c>, which the generated decorator enforces
    /// before the handler runs at all.
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("kinds")]
    public async Task WithAuthEnabled_IsDeniedToANonAdministrator(string operation) {
        using var host = AuthTestHost.Start(WithAuditModule, ("Watchtower:Auth:Enabled", "true"));
        await SeedEventsAsync(host, (AuthEventKinds.LoginOk, null, null, null));

        await using var scope = host.Services.CreateAsyncScope();
        TestPrincipal.Seed(scope.ServiceProvider, isAdmin: false);

        var kind = operation switch {
            "list" => await DeniedKindAsync<ListAuthEvents.Query, ListAuthEvents.Response>(
                scope.ServiceProvider, new ListAuthEvents.Query()),
            "kinds" => await DeniedKindAsync<ListAuthEventKinds.Query, ListAuthEventKinds.Response>(
                scope.ServiceProvider, new ListAuthEventKinds.Query()),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

        Assert.Equal(ErrorKind.Forbidden, kind);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("kinds")]
    public async Task WithAuthEnabled_IsAllowedToAnAdministrator(string operation) {
        using var host = AuthTestHost.Start(WithAuditModule, ("Watchtower:Auth:Enabled", "true"));
        await SeedEventsAsync(host, (AuthEventKinds.LoginOk, null, null, null));

        await using var scope = host.Services.CreateAsyncScope();
        TestPrincipal.Seed(scope.ServiceProvider, isAdmin: true);

        if (operation == "list") {
            var result = await SendAsync<ListAuthEvents.Query, ListAuthEvents.Response>(
                scope.ServiceProvider, new ListAuthEvents.Query());
            Assert.True(result.IsSuccess, Describe(result));
            Assert.Single(result.Value.Events);
        } else {
            var result = await SendAsync<ListAuthEventKinds.Query, ListAuthEventKinds.Response>(
                scope.ServiceProvider, new ListAuthEventKinds.Query());
            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal([AuthEventKinds.LoginOk], result.Value.Kinds);
        }
    }

    // -- Ordering and projection -----------------------------------------------------------------

    [Fact]
    public async Task List_ReturnsNewestFirst_ById() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var ids = await SeedEventsAsync(host,
            (AuthEventKinds.LoginOk, null, null, "first"),
            (AuthEventKinds.LoginFailed, null, null, "second"),
            (AuthEventKinds.Logout, null, null, "third"));

        var page = await ListAsync(host, new ListAuthEvents.Query());

        // Descending id, not descending CreatedAt: the SQLite provider cannot ORDER BY a DateTimeOffset,
        // and over an append-only table the surrogate key is the arrival order anyway.
        Assert.Equal([ids[2], ids[1], ids[0]], page.Events.Select(e => e.Id));
        Assert.Equal(["third", "second", "first"], page.Events.Select(e => e.Detail));
    }

    [Fact]
    public async Task List_NamesTheAccountAndTheAppTheRowMentions() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var userId = await host.AddUserAsync("alice");
        var route = await host.AddRouteAsync("app.example.invalid");
        await SeedEventsAsync(host, (AuthEventKinds.AccessDenied, userId, route.Id, "reason=no-grant"));

        var page = await ListAsync(host, new ListAuthEvents.Query());

        var row = Assert.Single(page.Events);
        Assert.Equal(AuthEventKinds.AccessDenied, row.Kind);
        Assert.Equal(userId, row.UserId);
        Assert.Equal("alice", row.UserName);
        Assert.Equal(route.Id, row.RouteId);
        Assert.Equal("app.example.invalid", row.RouteDomain);
        Assert.Equal("reason=no-grant", row.Detail);
    }

    [Fact]
    public async Task List_ProjectsARowWhoseSubjectsAreGone_WithoutNames() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var userId = await host.AddUserAsync("alice");
        await SeedEventsAsync(host, (AuthEventKinds.LoginOk, userId, null, "target=alice#1"));

        await DeleteUserAsync(host, userId);

        var page = await ListAsync(host, new ListAuthEvents.Query());

        // Both foreign keys are SET NULL on delete — the trail outlives its subjects — so the row survives
        // with its reference cleared. Naming the account is the Detail's job precisely because of this.
        var row = Assert.Single(page.Events);
        Assert.Null(row.UserId);
        Assert.Null(row.UserName);
        Assert.Equal("target=alice#1", row.Detail);
    }

    // -- Paging ----------------------------------------------------------------------------------

    [Fact]
    public async Task List_OnAnEmptyTrail_ReturnsNothingAndNoCursor() {
        using var host = AuthTestHost.Start(WithAuditModule);

        var page = await ListAsync(host, new ListAuthEvents.Query());

        Assert.Empty(page.Events);
        Assert.Null(page.NextBeforeId);
    }

    [Fact]
    public async Task List_AFullPage_ReportsACursor_AndAPartialOneDoesNot() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var ids = await SeedKindsAsync(host, AuthEventKinds.LoginOk, count: 5);

        var full = await ListAsync(host, new ListAuthEvents.Query(Limit: 5));
        var partial = await ListAsync(host, new ListAuthEvents.Query(Limit: 6));

        // A full page means "there may be more" — answered by a cursor rather than by counting the
        // remainder, which would be a second query over an unbounded table.
        Assert.Equal(5, full.Events.Count);
        Assert.Equal(ids[0], full.NextBeforeId);
        Assert.Equal(5, partial.Events.Count);
        Assert.Null(partial.NextBeforeId);
    }

    [Fact]
    public async Task List_FollowingTheCursor_WalksTheTrailWithoutGapsOrRepeats() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var ids = await SeedKindsAsync(host, AuthEventKinds.LoginOk, count: 5);

        var first = await ListAsync(host, new ListAuthEvents.Query(Limit: 2));
        var second = await ListAsync(host, new ListAuthEvents.Query(BeforeId: first.NextBeforeId, Limit: 2));
        var third = await ListAsync(host, new ListAuthEvents.Query(BeforeId: second.NextBeforeId, Limit: 2));

        Assert.Equal([ids[4], ids[3]], first.Events.Select(e => e.Id));
        Assert.Equal([ids[2], ids[1]], second.Events.Select(e => e.Id));
        // The last page came back short, so the walk ends here.
        Assert.Equal([ids[0]], third.Events.Select(e => e.Id));
        Assert.Null(third.NextBeforeId);
    }

    [Fact]
    public async Task List_TheCursorIsExclusive() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var ids = await SeedKindsAsync(host, AuthEventKinds.LoginOk, count: 3);

        var page = await ListAsync(host, new ListAuthEvents.Query(BeforeId: ids[1]));

        Assert.Equal([ids[0]], page.Events.Select(e => e.Id));
    }

    [Fact]
    public async Task List_ACursorPastTheOldestRow_ReturnsNothing() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var ids = await SeedKindsAsync(host, AuthEventKinds.LoginOk, count: 3);

        var page = await ListAsync(host, new ListAuthEvents.Query(BeforeId: ids[0]));

        Assert.Empty(page.Events);
        Assert.Null(page.NextBeforeId);
    }

    /// <summary>
    /// An over-large, zero or negative page size is clamped rather than refused: it is a client that has
    /// not been taught to page, not a caller doing something wrong.
    /// </summary>
    [Theory]
    [InlineData(null, AuditMapping.DefaultLimit)]
    [InlineData(0, AuditMapping.DefaultLimit)]
    [InlineData(-10, AuditMapping.DefaultLimit)]
    [InlineData(5_000, AuditMapping.MaxLimit)]
    public async Task List_ClampsThePageSize(int? limit, int expected) {
        using var host = AuthTestHost.Start(WithAuditModule);
        await SeedKindsAsync(host, AuthEventKinds.LoginOk, count: AuditMapping.MaxLimit + 1);

        var page = await ListAsync(host, new ListAuthEvents.Query(Limit: limit));

        Assert.Equal(expected, page.Events.Count);
        Assert.NotNull(page.NextBeforeId);
    }

    // -- Filters ---------------------------------------------------------------------------------

    [Fact]
    public async Task List_FiltersByKind() {
        using var host = AuthTestHost.Start(WithAuditModule);
        await SeedEventsAsync(host,
            (AuthEventKinds.LoginOk, null, null, null),
            (AuthEventKinds.LoginFailed, null, null, null),
            (AuthEventKinds.LoginOk, null, null, null));

        var page = await ListAsync(host, new ListAuthEvents.Query(Kind: AuthEventKinds.LoginOk));

        Assert.Equal(2, page.Events.Count);
        Assert.All(page.Events, e => Assert.Equal(AuthEventKinds.LoginOk, e.Kind));
    }

    [Fact]
    public async Task List_FiltersByKind_Exactly() {
        using var host = AuthTestHost.Start(WithAuditModule);
        await SeedEventsAsync(host,
            (AuthEventKinds.LoginOk, null, null, null),
            (AuthEventKinds.LoginFailed, null, null, null));

        // A prefix is not a match: "login" is not a kind, and treating it as one would silently turn the
        // dropdown's exact values into a search box.
        var page = await ListAsync(host, new ListAuthEvents.Query(Kind: "login"));

        Assert.Empty(page.Events);
    }

    [Fact]
    public async Task List_FiltersByUser() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");
        await SeedEventsAsync(host,
            (AuthEventKinds.LoginOk, alice, null, null),
            (AuthEventKinds.LoginOk, bob, null, null),
            (AuthEventKinds.LoginFailed, null, null, null));

        var page = await ListAsync(host, new ListAuthEvents.Query(UserId: alice));

        var row = Assert.Single(page.Events);
        Assert.Equal("alice", row.UserName);
    }

    [Fact]
    public async Task List_FiltersByRoute() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var app = await host.AddRouteAsync("app.example.invalid");
        var other = await host.AddRouteAsync("other.example.invalid");
        await SeedEventsAsync(host,
            (AuthEventKinds.AccessDenied, null, app.Id, null),
            (AuthEventKinds.AccessDenied, null, other.Id, null),
            (AuthEventKinds.LoginOk, null, null, null));

        var page = await ListAsync(host, new ListAuthEvents.Query(RouteId: app.Id));

        var row = Assert.Single(page.Events);
        Assert.Equal("app.example.invalid", row.RouteDomain);
    }

    [Fact]
    public async Task List_CombinesTheFiltersAndTheCursor() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");
        var app = await host.AddRouteAsync("app.example.invalid");
        var ids = await SeedEventsAsync(host,
            (AuthEventKinds.AccessDenied, alice, app.Id, "wanted-oldest"),
            (AuthEventKinds.AccessDenied, bob, app.Id, "wrong-user"),
            (AuthEventKinds.LoginFailed, alice, app.Id, "wrong-kind"),
            (AuthEventKinds.AccessDenied, alice, null, "wrong-route"),
            (AuthEventKinds.AccessDenied, alice, app.Id, "wanted-newest"),
            (AuthEventKinds.AccessDenied, alice, app.Id, "excluded-by-cursor"));

        var page = await ListAsync(host, new ListAuthEvents.Query(
            BeforeId: ids[5], Kind: AuthEventKinds.AccessDenied, UserId: alice, RouteId: app.Id));

        // The filters AND together, and the cursor narrows what is left rather than being applied first.
        Assert.Equal(["wanted-newest", "wanted-oldest"], page.Events.Select(e => e.Detail));
    }

    // -- Kinds -----------------------------------------------------------------------------------

    [Fact]
    public async Task Kinds_ListsWhatTheTrailContains_DistinctAndSorted() {
        using var host = AuthTestHost.Start(WithAuditModule);
        await SeedEventsAsync(host,
            (AuthEventKinds.LoginOk, null, null, null),
            (AuthEventKinds.AccessDenied, null, null, null),
            (AuthEventKinds.LoginOk, null, null, null),
            (AuthEventKinds.UserCreated, null, null, null));

        var kinds = await KindsAsync(host);

        // Read off the rows rather than off AuthEventKinds, so the filter offers what is actually there —
        // and so a kind a future writer introduces becomes filterable without a frontend edit.
        Assert.Equal([AuthEventKinds.AccessDenied, AuthEventKinds.LoginOk, AuthEventKinds.UserCreated], kinds);
    }

    [Fact]
    public async Task Kinds_OnAnEmptyTrail_IsEmpty() {
        using var host = AuthTestHost.Start(WithAuditModule);

        Assert.Empty(await KindsAsync(host));
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

    private static async Task<ListAuthEvents.Response> ListAsync(AuthTestHost host, ListAuthEvents.Query query) {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListAuthEvents.Query, ListAuthEvents.Response>(scope.ServiceProvider, query);
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value;
    }

    private static async Task<IReadOnlyList<string>> KindsAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListAuthEventKinds.Query, ListAuthEventKinds.Response>(
            scope.ServiceProvider, new ListAuthEventKinds.Query());
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value.Kinds;
    }

    /// <summary>
    /// Appends rows to the trail in the order given and returns their ids. Written directly rather than
    /// through the modules that record them: the rows are the <em>precondition</em> of the reader under
    /// test, and going through the writers would mean seeding a login endpoint to get a `login.failed`.
    /// </summary>
    private static async Task<IReadOnlyList<int>> SeedEventsAsync(
        AuthTestHost host, params (string Kind, int? UserId, int? RouteId, string? Detail)[] rows) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var events = rows.Select(r => new AuthEvent {
            Kind = r.Kind,
            UserId = r.UserId,
            RouteId = r.RouteId,
            Detail = r.Detail,
            CreatedAt = host.Time.GetUtcNow(),
        }).ToList();
        db.AuthEvents.AddRange(events);
        await db.SaveChangesAsync(Ct);
        return [.. events.Select(e => e.Id)];
    }

    /// <summary>Appends <paramref name="count"/> rows of one kind and returns their ids, oldest first.</summary>
    private static Task<IReadOnlyList<int>> SeedKindsAsync(AuthTestHost host, string kind, int count) =>
        SeedEventsAsync(host, [.. Enumerable.Range(0, count).Select(i => (kind, (int?)null, (int?)null, (string?)$"#{i}"))]);

    /// <summary>Deletes an account through the store the application itself writes with.</summary>
    private static async Task DeleteUserAsync(AuthTestHost host, int userId) {
        await using var scope = host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.FindByIdAsync(userId.ToString());
        Assert.NotNull(user);
        var deleted = await users.DeleteAsync(user);
        Assert.True(deleted.Succeeded, string.Join("; ", deleted.Errors.Select(e => e.Description)));
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
