using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Proxy;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The write surface for access rules and their attachment to routes (ADR-0039): what
/// <c>proxy.setAccessRule</c> refuses before it writes, what <c>proxy.setAccess</c> refuses to attach, and
/// the save-time portability check that is the whole reason portability is declared.
/// </summary>
/// <remarks>
/// The refusals are the subject, not an afterthought. A projection that silently drops half of what was asked
/// for is the failure this feature exists to prevent, so the place an operator finds out has to be the save.
/// </remarks>
public sealed class AccessRuleHandlerTests {
    private static readonly Action<IServiceCollection> WithAccessHandlers = services => {
        services.AddGetAccess();
        services.AddSetAccess();
        services.AddListAccessRules();
        services.AddSetAccessRule();
        services.AddDeleteAccessRule();
        services.RemoveAll<IProxyProvider>();
        services.AddSingleton<IProxyProvider, RecordingProxyProvider>();
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A host on the Cloudflare provider — the edge most clause kinds are enforceable at.</summary>
    private static AuthTestHost CloudflareHost() =>
        AuthTestHost.Start(WithAccessHandlers, [
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "cloudflare"),
        ]);

    /// <summary>A host on the in-process provider, where the edge-only clause kinds cannot be honoured.</summary>
    private static AuthTestHost YarpHost() =>
        AuthTestHost.Start(WithAccessHandlers, [
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "yarp"),
        ]);

    // ── Creating and editing a rule ──────────────────────────────────────────

    [Fact]
    public async Task ARuleIsCreatedWithItsClauses_AndReportsItsPortability() {
        using var host = CloudflareHost();

        var result = await SetRuleAsync(host, new SetAccessRule.Command(
            "friends",
            [new AccessRuleClauseDto(AccessClauseKind.Email, Value: "friend@elsewhere.com")],
            Description: "the people I let into the shared apps"));

        Assert.True(result.IsSuccess, Describe(result));
        var rule = result.Value.Rule;
        Assert.Equal("friends", rule.Name);
        Assert.Equal("the people I let into the shared apps", rule.Description);
        var clause = Assert.Single(rule.Clauses);
        Assert.Equal(AccessClauseKind.Email, clause.Kind);
        // An email clause is edge-only, so the rule reports exactly that rather than claiming to be portable.
        Assert.False(rule.EnforceableInProcess);
        Assert.True(rule.EnforceableByCloudflareAccess);
        Assert.Equal(0, rule.AttachedRouteCount);
    }

    [Fact]
    public async Task ARuleWithEdgeOnlyClauses_IsStillCreatableUnderYarp() {
        // Deliberate (ADR-0039 decision 4): keeping both spellings on a rule is how a move between edges is
        // staged. What is refused is attaching it to a route this provider would then mis-serve.
        using var host = YarpHost();

        var result = await SetRuleAsync(host, new SetAccessRule.Command(
            "friends", [new AccessRuleClauseDto(AccessClauseKind.ExternalPolicy, Value: "pol-friends")]));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.False(result.Value.Rule.EnforceableInProcess);
    }

    [Fact]
    public async Task EditingARule_ReplacesItsClauses_AndKeepsItsId() {
        using var host = CloudflareHost();
        var created = await SetRuleAsync(host, new SetAccessRule.Command(
            "family", [new AccessRuleClauseDto(AccessClauseKind.Email, Value: "old@example.com")]));

        var edited = await SetRuleAsync(host, new SetAccessRule.Command(
            "family",
            [new AccessRuleClauseDto(AccessClauseKind.ExternalPolicy, Value: "pol-family")],
            Id: created.Value.Rule.Id));

        Assert.True(edited.IsSuccess, Describe(edited));
        Assert.Equal(created.Value.Rule.Id, edited.Value.Rule.Id);
        var clause = Assert.Single(edited.Value.Rule.Clauses);
        Assert.Equal(AccessClauseKind.ExternalPolicy, clause.Kind);
        Assert.Equal("pol-family", clause.Value);
    }

    [Fact]
    public async Task ASecondRuleWithTheSameName_IsRefused() {
        using var host = CloudflareHost();
        await SetRuleAsync(host, new SetAccessRule.Command("family", []));

        // Case-blind, like every other name in the instance.
        var result = await SetRuleAsync(host, new SetAccessRule.Command("FAMILY", []));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
    }

    [Theory]
    [InlineData(AccessClauseKind.Email, "not-an-address")]
    [InlineData(AccessClauseKind.EmailDomain, "someone@example.com")]
    [InlineData(AccessClauseKind.EmailDomain, "localhost")]
    public async Task AMalformedClauseValue_IsRefused(AccessClauseKind kind, string value) {
        using var host = CloudflareHost();

        var result = await SetRuleAsync(host, new SetAccessRule.Command(
            "bad", [new AccessRuleClauseDto(kind, Value: value)]));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task AClauseCarryingTheWrongSubjectColumn_IsRefused() {
        // The CHECK constraint would settle this anyway; the refusal exists so it reads as a message rather
        // than a database exception.
        using var host = CloudflareHost();

        var result = await SetRuleAsync(host, new SetAccessRule.Command(
            "confused", [new AccessRuleClauseDto(AccessClauseKind.Email, UserId: 1, Value: "a@b.com")]));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    [Fact]
    public async Task AClauseNamingAnUnknownGroup_IsRefusedWholeRatherThanHalfApplied() {
        using var host = CloudflareHost();

        var result = await SetRuleAsync(host, new SetAccessRule.Command("mixed", [
            new AccessRuleClauseDto(AccessClauseKind.Email, Value: "friend@elsewhere.com"),
            new AccessRuleClauseDto(AccessClauseKind.Group, GroupId: 4242),
        ]));

        Assert.False(result.IsSuccess);
        Assert.Contains("4242", result.Error.Message, StringComparison.Ordinal);
        // Nothing was written: the good clause must not survive the bad one.
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Empty(await db.AccessRules.ToListAsync(Ct));
    }

    [Fact]
    public async Task AClauseNamingAnAccountOfAnotherRealm_IsRefused() {
        using var host = CloudflareHost();
        var otherRealmId = await host.AddRealmAsync("tenants");
        var foreignUser = await host.AddUserAsync("tenant-admin", realmId: otherRealmId);

        var result = await SetRuleAsync(host, new SetAccessRule.Command(
            "operators", [new AccessRuleClauseDto(AccessClauseKind.User, UserId: foreignUser)]));

        Assert.False(result.IsSuccess);
        Assert.Contains("different realm", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamingTheSameSubjectTwice_IsOneClause() {
        using var host = CloudflareHost();

        var result = await SetRuleAsync(host, new SetAccessRule.Command("friends", [
            new AccessRuleClauseDto(AccessClauseKind.Email, Value: "friend@elsewhere.com"),
            new AccessRuleClauseDto(AccessClauseKind.Email, Value: "FRIEND@elsewhere.com"),
        ]));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Single(result.Value.Rule.Clauses);
    }

    // ── Attaching to a route ─────────────────────────────────────────────────

    [Fact]
    public async Task AttachingTwoRules_StoresThemInTheGivenOrder() {
        // The composition case, through the write surface: order is precedence at the edge, so it round-trips.
        using var host = CloudflareHost();
        var route = await host.AddRouteAsync("shared.example.invalid", AccessMode.Authenticated);
        var family = await CreatedRuleIdAsync(host, "family", "pol-family");
        var friends = await CreatedRuleIdAsync(host, "friends", "pol-friends");

        var result = await SetAccessAsync(host, new SetAccess.Command(
            route.Id, AccessMode.Authenticated, BypassPaths: null, GrantedUserIds: [],
            AccessRuleIds: [family, friends]));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal([family, friends], result.Value.AccessRuleIds);
        var read = await GetAccessAsync(host, route.Id);
        Assert.Equal([family, friends], read.Value.AccessRuleIds);
        Assert.Equal(ActiveEnforcementPoint.CloudflareAccess, read.Value.ActiveEnforcementPoint);
    }

    [Fact]
    public async Task ReSavingTheSameAttachments_ChurnsNoRows() {
        using var host = CloudflareHost();
        var route = await host.AddRouteAsync("shared.example.invalid", AccessMode.Authenticated);
        var family = await CreatedRuleIdAsync(host, "family", "pol-family");
        var command = new SetAccess.Command(
            route.Id, AccessMode.Authenticated, BypassPaths: null, GrantedUserIds: [], AccessRuleIds: [family]);
        await SetAccessAsync(host, command);
        var before = await AttachmentIdsAsync(host, route.Id);

        await SetAccessAsync(host, command);

        Assert.Equal(before, await AttachmentIdsAsync(host, route.Id));
    }

    [Fact]
    public async Task AttachingAnEmptyList_DetachesEverything_AndRestoresTheInstanceWideFallback() {
        using var host = CloudflareHost();
        var route = await host.AddRouteAsync("shared.example.invalid", AccessMode.Authenticated);
        var family = await CreatedRuleIdAsync(host, "family", "pol-family");
        await SetAccessAsync(host, new SetAccess.Command(
            route.Id, AccessMode.Authenticated, BypassPaths: null, GrantedUserIds: [], AccessRuleIds: [family]));

        await SetAccessAsync(host, new SetAccess.Command(
            route.Id, AccessMode.Authenticated, BypassPaths: null, GrantedUserIds: [], AccessRuleIds: []));

        Assert.Empty(await AttachmentIdsAsync(host, route.Id));
    }

    [Fact]
    public async Task AClientThatOmitsTheField_LeavesAttachmentsAlone() {
        // The backward-compatibility property, and the one place this parameter deliberately differs from the
        // grant lists: omitting the field is "I have nothing to say about rules", which must not be read as
        // "detach everything" — otherwise an old UI saving a route's access silently strips a composition
        // built in a new one, and the hostname falls back to the instance-wide list with nothing saying so.
        using var host = CloudflareHost();
        var route = await host.AddRouteAsync("shared.example.invalid", AccessMode.Authenticated);
        var family = await CreatedRuleIdAsync(host, "family", "pol-family");
        await host.AttachAccessRuleAsync(route.Id, family);

        var result = await SetAccessAsync(host, new SetAccess.Command(
            route.Id, AccessMode.Authenticated, BypassPaths: null, GrantedUserIds: []));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal([family], await AttachmentIdsAsync(host, route.Id));
        // And the response reports what the route actually has, not the empty list the caller implied.
        Assert.Equal([family], result.Value.AccessRuleIds);
    }

    [Fact]
    public async Task LeavingAuthenticated_ClearsAttachments_EvenWhenTheFieldIsOmitted() {
        // The mode is what makes attachments mean anything, so switching away clears them exactly as it
        // clears grants — otherwise a route flipped to Public and back would silently regain an allow-list.
        using var host = CloudflareHost();
        var route = await host.AddRouteAsync("shared.example.invalid", AccessMode.Authenticated);
        var family = await CreatedRuleIdAsync(host, "family", "pol-family");
        await host.AttachAccessRuleAsync(route.Id, family);

        var result = await SetAccessAsync(host, new SetAccess.Command(
            route.Id, AccessMode.Public, BypassPaths: null, GrantedUserIds: []));

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Empty(await AttachmentIdsAsync(host, route.Id));
    }

    [Fact]
    public async Task AttachingARuleToARestrictedRoute_IsRefused() {
        // Restricted means "exactly these subjects" (ADR-0039 decision 2). Refused rather than silently
        // dropped: an administrator who thought they had composed two allow-lists must find out.
        using var host = CloudflareHost();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);
        var family = await CreatedRuleIdAsync(host, "family", "pol-family");

        var result = await SetAccessAsync(host, new SetAccess.Command(
            route.Id, AccessMode.Restricted, BypassPaths: null, GrantedUserIds: [], AccessRuleIds: [family]));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
        Assert.Contains("Restricted", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttachingARuleThisProviderCannotEnforce_IsRefusedAtSaveTime() {
        // The point of declaring portability: a reconcile is never where an operator learns that half of what
        // they asked for was dropped.
        using var host = YarpHost();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);
        var friends = await CreatedRuleIdAsync(host, "friends", "pol-friends");

        var result = await SetAccessAsync(host, new SetAccess.Command(
            route.Id, AccessMode.Authenticated, BypassPaths: null, GrantedUserIds: [], AccessRuleIds: [friends]));

        Assert.False(result.IsSuccess);
        Assert.Contains("external policy clauses", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("forward-auth", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AttachingAPortableRule_IsAcceptedUnderEitherProvider() {
        using var host = YarpHost();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);
        var alice = await host.AddUserAsync("alice");
        var groupId = await host.AddGroupAsync("staff", alice);
        var staff = await SetRuleAsync(host, new SetAccessRule.Command(
            "staff", [new AccessRuleClauseDto(AccessClauseKind.Group, GroupId: groupId)]));

        var result = await SetAccessAsync(host, new SetAccess.Command(
            route.Id, AccessMode.Authenticated, BypassPaths: null, GrantedUserIds: [],
            AccessRuleIds: [staff.Value.Rule.Id]));

        Assert.True(result.IsSuccess, Describe(result));
    }

    [Fact]
    public async Task AttachingAnUnknownRule_IsRefused() {
        using var host = CloudflareHost();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);

        var result = await SetAccessAsync(host, new SetAccess.Command(
            route.Id, AccessMode.Authenticated, BypassPaths: null, GrantedUserIds: [], AccessRuleIds: [4242]));

        Assert.False(result.IsSuccess);
        Assert.Contains("4242", result.Error.Message, StringComparison.Ordinal);
    }

    // ── Deleting ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeletingAnAttachedRule_IsRefused_AndNamesTheRoutes() {
        using var host = CloudflareHost();
        var route = await host.AddRouteAsync("shared.example.invalid", AccessMode.Authenticated);
        var family = await CreatedRuleIdAsync(host, "family", "pol-family");
        await SetAccessAsync(host, new SetAccess.Command(
            route.Id, AccessMode.Authenticated, BypassPaths: null, GrantedUserIds: [], AccessRuleIds: [family]));

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<DeleteAccessRule.Command, DeleteAccessRule.Response>(
            scope.ServiceProvider, new DeleteAccessRule.Command(family));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        // The hostname, not a count: "detach it from this one" is actionable.
        Assert.Contains("shared.example.invalid", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingADetachedRule_TakesItsClausesWithIt() {
        using var host = CloudflareHost();
        var family = await CreatedRuleIdAsync(host, "family", "pol-family");

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<DeleteAccessRule.Command, DeleteAccessRule.Response>(
            scope.ServiceProvider, new DeleteAccessRule.Command(family));

        Assert.True(result.IsSuccess, Describe(result));
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Empty(await db.AccessRules.ToListAsync(Ct));
        Assert.Empty(await db.AccessRuleClauses.ToListAsync(Ct));
    }

    [Fact]
    public async Task TheListing_ReportsAttachmentCountsAndClauseLabels() {
        using var host = CloudflareHost();
        var route = await host.AddRouteAsync("shared.example.invalid", AccessMode.Authenticated);
        var alice = await host.AddUserAsync("alice");
        var staff = await SetRuleAsync(host, new SetAccessRule.Command(
            "staff", [new AccessRuleClauseDto(AccessClauseKind.User, UserId: alice)]));
        await SetAccessAsync(host, new SetAccess.Command(
            route.Id, AccessMode.Authenticated, BypassPaths: null, GrantedUserIds: [],
            AccessRuleIds: [staff.Value.Rule.Id]));

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<ListAccessRules.Query, ListAccessRules.Response>(
            scope.ServiceProvider, new ListAccessRules.Query());

        Assert.True(result.IsSuccess, Describe(result));
        var rule = Assert.Single(result.Value.Rules);
        Assert.Equal(1, rule.AttachedRouteCount);
        // The label is what a picker shows, so a user clause reads as the account's name, not its id.
        Assert.Equal("alice", Assert.Single(rule.Clauses).SubjectLabel);
        Assert.True(rule.EnforceableInProcess);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static async Task<int> CreatedRuleIdAsync(AuthTestHost host, string name, string externalPolicyId) {
        var result = await SetRuleAsync(host, new SetAccessRule.Command(
            name, [new AccessRuleClauseDto(AccessClauseKind.ExternalPolicy, Value: externalPolicyId)]));
        Assert.True(result.IsSuccess, Describe(result));
        return result.Value.Rule.Id;
    }

    private static async Task<IReadOnlyList<int>> AttachmentIdsAsync(AuthTestHost host, int routeId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.RouteAccessRules.AsNoTracking()
            .Where(a => a.RouteId == routeId)
            .OrderBy(a => a.Order)
            .Select(a => a.AccessRuleId)
            .ToListAsync(Ct);
    }

    private static async Task<Result<SetAccessRule.Response>> SetRuleAsync(
        AuthTestHost host, SetAccessRule.Command command) {
        await using var scope = host.Services.CreateAsyncScope();
        return await SendAsync<SetAccessRule.Command, SetAccessRule.Response>(scope.ServiceProvider, command);
    }

    private static async Task<Result<SetAccess.Response>> SetAccessAsync(
        AuthTestHost host, SetAccess.Command command) {
        await using var scope = host.Services.CreateAsyncScope();
        return await SendAsync<SetAccess.Command, SetAccess.Response>(scope.ServiceProvider, command);
    }

    private static async Task<Result<GetAccess.Response>> GetAccessAsync(AuthTestHost host, int routeId) {
        await using var scope = host.Services.CreateAsyncScope();
        return await SendAsync<GetAccess.Query, GetAccess.Response>(
            scope.ServiceProvider, new GetAccess.Query(routeId));
    }

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>().HandleAsync(request, Ct);

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
