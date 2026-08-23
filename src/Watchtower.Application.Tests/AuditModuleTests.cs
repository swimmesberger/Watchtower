using Elarion.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Audit.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the Audit module — the read-only view over the one trail every plane writes — through its
/// real generated handler pipelines, so the <c>[RequireRole("Admin")]</c> decorator is exercised
/// alongside the paging arithmetic, the filters and the facets.
/// </summary>
public sealed class AuditModuleTests {
    /// <summary>Both Audit handlers, added the way the generated module registration does.</summary>
    private static readonly Action<IServiceCollection> WithAuditModule = services => {
        services.AddListAuditEvents();
        services.AddListAuditFacets();
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // -- Authorization ---------------------------------------------------------------------------

    /// <summary>
    /// The trail names accounts, hostnames and apps across every realm, so reading it is an
    /// instance-administration act. Both handlers carry <c>[RequireRole("Admin")]</c>, which the
    /// generated decorator enforces before the handler runs at all.
    /// </summary>
    [Theory]
    [InlineData("list")]
    [InlineData("facets")]
    public async Task WithAuthEnabled_IsDeniedToANonAdministrator(string operation) {
        using var host = AuthTestHost.Start(WithAuditModule, ("Watchtower:Auth:Enabled", "true"));
        await SeedAsync(host, ("auth", AuthEventKinds.LoginOk, "alice", "alice", null, true));

        await using var scope = host.Services.CreateAsyncScope();
        TestPrincipal.Seed(scope.ServiceProvider, isAdmin: false);

        var kind = operation switch {
            "list" => await DeniedKindAsync<ListAuditEvents.Query, ListAuditEvents.Response>(
                scope.ServiceProvider, new ListAuditEvents.Query()),
            "facets" => await DeniedKindAsync<ListAuditFacets.Query, ListAuditFacets.Response>(
                scope.ServiceProvider, new ListAuditFacets.Query()),
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null),
        };

        Assert.Equal(ErrorKind.Forbidden, kind);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("facets")]
    public async Task WithAuthEnabled_IsAllowedToAnAdministrator(string operation) {
        using var host = AuthTestHost.Start(WithAuditModule, ("Watchtower:Auth:Enabled", "true"));
        await SeedAsync(host, ("auth", AuthEventKinds.LoginOk, "alice", "alice", null, true));

        await using var scope = host.Services.CreateAsyncScope();
        TestPrincipal.Seed(scope.ServiceProvider, isAdmin: true);

        if (operation == "list") {
            var result = await SendAsync<ListAuditEvents.Query, ListAuditEvents.Response>(
                scope.ServiceProvider, new ListAuditEvents.Query());
            Assert.True(result.IsSuccess, Describe(result));
            Assert.Single(result.Value.Events);
        } else {
            var result = await SendAsync<ListAuditFacets.Query, ListAuditFacets.Response>(
                scope.ServiceProvider, new ListAuditFacets.Query());
            Assert.True(result.IsSuccess, Describe(result));
            Assert.Equal(["auth"], result.Value.Categories);
            Assert.Equal([AuthEventKinds.LoginOk], result.Value.Actions);
            Assert.Equal(["alice"], result.Value.Actors);
        }
    }

    // -- Ordering and projection -----------------------------------------------------------------

    [Fact]
    public async Task List_ReturnsNewestFirst_ById() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var ids = await SeedAsync(host,
            ("auth", AuthEventKinds.LoginOk, "alice", "alice", "first", true),
            ("auth", AuthEventKinds.LoginFailed, "alice", null, "second", false),
            ("auth", AuthEventKinds.Logout, "alice", "alice", "third", true));

        var page = await ListAsync(host, new ListAuditEvents.Query());

        // Descending id, not descending CreatedAt: id is unique, so it is the only total order,
        // and over an append-only table the surrogate key is the arrival order anyway.
        Assert.Equal([ids[2], ids[1], ids[0]], page.Events.Select(e => e.Id));
        Assert.Equal(["third", "second", "first"], page.Events.Select(e => e.Detail));
    }

    [Fact]
    public async Task List_ProjectsEveryColumn() {
        using var host = AuthTestHost.Start(WithAuditModule);
        await SeedAsync(host, ("access", AuthEventKinds.AccessDenied, "app.example.invalid", "alice", "reason=no-grant", false));

        var page = await ListAsync(host, new ListAuditEvents.Query());

        var row = Assert.Single(page.Events);
        Assert.Equal("access", row.Category);
        Assert.Equal(AuthEventKinds.AccessDenied, row.Action);
        Assert.Equal("app.example.invalid", row.Target);
        Assert.Equal("alice", row.Actor);
        Assert.Equal("reason=no-grant", row.Detail);
        Assert.False(row.Success);
    }

    // -- Paging ----------------------------------------------------------------------------------

    [Fact]
    public async Task List_OnAnEmptyTrail_ReturnsNothingAndNoCursor() {
        using var host = AuthTestHost.Start(WithAuditModule);

        var page = await ListAsync(host, new ListAuditEvents.Query());

        Assert.Empty(page.Events);
        Assert.Null(page.NextBeforeId);
    }

    [Fact]
    public async Task List_AFullPage_ReportsACursor_AndAPartialOneDoesNot() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var ids = await SeedManyAsync(host, count: 5);

        var full = await ListAsync(host, new ListAuditEvents.Query(Limit: 5));
        var partial = await ListAsync(host, new ListAuditEvents.Query(Limit: 6));

        // A full page means "there may be more" — answered by a cursor rather than by counting the
        // remainder, which would be a second query over the whole table.
        Assert.Equal(5, full.Events.Count);
        Assert.Equal(ids[0], full.NextBeforeId);
        Assert.Equal(5, partial.Events.Count);
        Assert.Null(partial.NextBeforeId);
    }

    [Fact]
    public async Task List_FollowingTheCursor_WalksTheTrailWithoutGapsOrRepeats() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var ids = await SeedManyAsync(host, count: 5);

        var first = await ListAsync(host, new ListAuditEvents.Query(Limit: 2));
        var second = await ListAsync(host, new ListAuditEvents.Query(BeforeId: first.NextBeforeId, Limit: 2));
        var third = await ListAsync(host, new ListAuditEvents.Query(BeforeId: second.NextBeforeId, Limit: 2));

        Assert.Equal([ids[4], ids[3]], first.Events.Select(e => e.Id));
        Assert.Equal([ids[2], ids[1]], second.Events.Select(e => e.Id));
        // The last page came back short, so the walk ends here.
        Assert.Equal([ids[0]], third.Events.Select(e => e.Id));
        Assert.Null(third.NextBeforeId);
    }

    [Fact]
    public async Task List_TheCursorIsExclusive() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var ids = await SeedManyAsync(host, count: 3);

        var page = await ListAsync(host, new ListAuditEvents.Query(BeforeId: ids[1]));

        Assert.Equal([ids[0]], page.Events.Select(e => e.Id));
    }

    /// <summary>
    /// An over-large, zero or negative page size is clamped rather than refused: it is a client that has
    /// not been taught to page, not a caller doing something wrong.
    /// </summary>
    [Theory]
    [InlineData(null, AuditPaging.DefaultLimit)]
    [InlineData(0, AuditPaging.DefaultLimit)]
    [InlineData(-10, AuditPaging.DefaultLimit)]
    [InlineData(5_000, AuditPaging.MaxLimit)]
    public async Task List_ClampsThePageSize(int? limit, int expected) {
        using var host = AuthTestHost.Start(WithAuditModule);
        await SeedManyAsync(host, count: AuditPaging.MaxLimit + 1);

        var page = await ListAsync(host, new ListAuditEvents.Query(Limit: limit));

        Assert.Equal(expected, page.Events.Count);
        Assert.NotNull(page.NextBeforeId);
    }

    // -- Filters ---------------------------------------------------------------------------------

    [Fact]
    public async Task List_FiltersByCategoryPrefix_OnDottedSegments() {
        using var host = AuthTestHost.Start(WithAuditModule);
        await SeedAsync(host,
            ("proxy.cloudflare", "dns.create", "a.example.com", null, null, true),
            ("proxyx", "other", "b", null, null, true),
            ("backups", "run", "shop", null, null, true));

        var proxyOnly = await ListAsync(host, new ListAuditEvents.Query(Category: "proxy"));
        var row = Assert.Single(proxyOnly.Events);
        Assert.Equal("proxy.cloudflare", row.Category);

        var exact = await ListAsync(host, new ListAuditEvents.Query(Category: "proxy.cloudflare"));
        Assert.Single(exact.Events);
    }

    [Fact]
    public async Task List_FiltersByAction_Exactly() {
        using var host = AuthTestHost.Start(WithAuditModule);
        await SeedAsync(host,
            ("auth", AuthEventKinds.LoginOk, "alice", "alice", null, true),
            ("auth", AuthEventKinds.LoginFailed, "alice", null, null, false),
            ("auth", AuthEventKinds.LoginOk, "bob", "bob", null, true));

        var page = await ListAsync(host, new ListAuditEvents.Query(Action: AuthEventKinds.LoginOk));
        Assert.Equal(2, page.Events.Count);
        Assert.All(page.Events, e => Assert.Equal(AuthEventKinds.LoginOk, e.Action));

        // A prefix is not a match: "login" is not an action, and treating it as one would silently turn
        // the dropdown's exact values into a search box.
        Assert.Empty((await ListAsync(host, new ListAuditEvents.Query(Action: "login"))).Events);
    }

    [Fact]
    public async Task List_FiltersByActor_AndSystemSelectsRowsWithout() {
        using var host = AuthTestHost.Start(WithAuditModule);
        await SeedAsync(host,
            ("auth", AuthEventKinds.LoginOk, "alice", "alice", null, true),
            ("auth", AuthEventKinds.LoginOk, "bob", "bob", null, true),
            ("proxy.cloudflare", "dns.create", "a.example.com", null, null, true));

        var alice = await ListAsync(host, new ListAuditEvents.Query(Actor: "alice"));
        Assert.Equal("alice", Assert.Single(alice.Events).Actor);

        var system = await ListAsync(host, new ListAuditEvents.Query(Actor: ListAuditEvents.SystemActor));
        Assert.Null(Assert.Single(system.Events).Actor);
    }

    [Fact]
    public async Task List_CombinesTheFiltersAndTheCursor() {
        using var host = AuthTestHost.Start(WithAuditModule);
        var ids = await SeedAsync(host,
            ("access", AuthEventKinds.AccessDenied, "app", "alice", "wanted-oldest", false),
            ("access", AuthEventKinds.AccessDenied, "app", "bob", "wrong-actor", false),
            ("auth", AuthEventKinds.LoginFailed, "alice", "alice", "wrong-category", false),
            ("access", "route.access.changed", "app", "alice", "wrong-action", true),
            ("access", AuthEventKinds.AccessDenied, "app", "alice", "wanted-newest", false),
            ("access", AuthEventKinds.AccessDenied, "app", "alice", "excluded-by-cursor", false));

        var page = await ListAsync(host, new ListAuditEvents.Query(
            Category: "access", Action: AuthEventKinds.AccessDenied, Actor: "alice", BeforeId: ids[5]));

        // The filters AND together, and the cursor narrows what is left rather than being applied first.
        Assert.Equal(["wanted-newest", "wanted-oldest"], page.Events.Select(e => e.Detail));
    }

    // -- Facets ----------------------------------------------------------------------------------

    [Fact]
    public async Task Facets_ListWhatTheTrailContains_DistinctAndSorted() {
        using var host = AuthTestHost.Start(WithAuditModule);
        await SeedAsync(host,
            ("auth", AuthEventKinds.LoginOk, "bob", "bob", null, true),
            ("access", AuthEventKinds.AccessDenied, "app", "alice", null, false),
            ("auth", AuthEventKinds.LoginOk, "alice", "alice", null, true),
            ("proxy.cloudflare", "dns.create", "a.example.com", null, null, true));

        var facets = await FacetsAsync(host);

        // Read off the rows rather than off a vocabulary, so the filters offer what is actually there —
        // and so a category or action a future writer introduces becomes filterable without a frontend edit.
        Assert.Equal(["access", "auth", "proxy.cloudflare"], facets.Categories);
        Assert.Equal([AuthEventKinds.AccessDenied, "dns.create", AuthEventKinds.LoginOk], facets.Actions);
        Assert.Equal(["alice", "bob", ListAuditEvents.SystemActor], facets.Actors);
    }

    [Fact]
    public async Task Facets_OnAnEmptyTrail_AreEmpty() {
        using var host = AuthTestHost.Start(WithAuditModule);

        var facets = await FacetsAsync(host);

        Assert.Empty(facets.Categories);
        Assert.Empty(facets.Actions);
        Assert.Empty(facets.Actors);
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

    private static async Task<ListAuditEvents.Response> ListAsync(AuthTestHost host, ListAuditEvents.Query query) {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListAuditEvents.Query, ListAuditEvents.Response>(scope.ServiceProvider, query);
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value;
    }

    private static async Task<ListAuditFacets.Response> FacetsAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListAuditFacets.Query, ListAuditFacets.Response>(
            scope.ServiceProvider, new ListAuditFacets.Query());
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value;
    }

    /// <summary>
    /// Appends rows to the trail in the order given and returns their ids. Written directly rather than
    /// through the writers: the rows are the <em>precondition</em> of the reader under test, and going
    /// through the writers would mean seeding a login endpoint to get a `login.failed`.
    /// </summary>
    private static async Task<IReadOnlyList<int>> SeedAsync(
        AuthTestHost host,
        params (string Category, string Action, string Target, string? Actor, string? Detail, bool Success)[] rows) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var events = rows.Select(r => new AuditEvent {
            Category = r.Category,
            Action = r.Action,
            Target = r.Target,
            Actor = r.Actor,
            Detail = r.Detail,
            Success = r.Success,
            CreatedAt = host.Time.GetUtcNow(),
        }).ToList();
        db.AuditEvents.AddRange(events);
        await db.SaveChangesAsync(Ct);
        return [.. events.Select(e => e.Id)];
    }

    /// <summary>Appends <paramref name="count"/> login rows and returns their ids, oldest first.</summary>
    private static Task<IReadOnlyList<int>> SeedManyAsync(AuthTestHost host, int count) =>
        SeedAsync(host, [.. Enumerable.Range(0, count)
            .Select(i => ("auth", AuthEventKinds.LoginOk, "alice", (string?)"alice", (string?)$"#{i}", true))]);

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
