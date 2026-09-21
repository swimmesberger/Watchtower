using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Resolution: how a route's attached <see cref="AccessRule"/>s become the flat allow-list an enforcement
/// point applies (ADR-0039), and the invariants that must survive the flattening.
/// </summary>
/// <remarks>
/// The realm cases are the security-relevant ones. <c>RouteAccessPolicy</c> guarantees in process that a
/// protected route is only reachable by an account of its own realm, whatever its grants say; resolution is
/// where that guarantee either crosses the provider seam or is quietly lost (ADR-0040 decision 3).
/// </remarks>
public sealed class AccessRuleResolverTests {
    [Fact]
    public async Task AnUnattachedRoute_IsAbsent_NotPresentAndEmpty() {
        // The distinction the whole fallback rests on: absent means "use the instance-wide settings", while
        // present-and-empty means "admit nobody". Collapsing them would publish a hostname the operator
        // believes is gated, or gate one they believe is open.
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);

        var resolved = await ResolveAsync(host, route.Id);

        Assert.Empty(resolved);
    }

    [Fact]
    public async Task ARouteWithRules_ResolvesEveryClauseKind() {
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);
        var userId = await host.AddUserAsync("alice");
        await host.SetEmailAsync(userId, "alice@example.com");
        var ruleId = await host.AddAccessRuleAsync("mixed", [
            Clause(AccessClauseKind.User, userId: userId),
            Clause(AccessClauseKind.Email, value: "friend@elsewhere.com"),
            Clause(AccessClauseKind.EmailDomain, value: "example.com"),
            Clause(AccessClauseKind.ExternalGroup, value: "grp-entra"),
            Clause(AccessClauseKind.ExternalPolicy, value: "pol-family"),
        ]);
        await host.AttachAccessRuleAsync(route.Id, ruleId);

        var access = (await ResolveAsync(host, route.Id))[route.Id];

        Assert.Equal(["alice@example.com", "friend@elsewhere.com"], access.Emails);
        Assert.Equal(["example.com"], access.EmailDomains);
        Assert.Equal(["grp-entra"], access.ExternalGroupIds);
        Assert.Equal(["pol-family"], access.ExternalPolicyIds);
        Assert.Equal(["mixed"], access.RuleNames);
        Assert.Empty(access.UnsupportedKinds);
        Assert.False(access.IsEmpty);
    }

    [Fact]
    public async Task AGroupClause_ContributesItsMembersAddresses() {
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);
        var alice = await host.AddUserAsync("alice");
        var bob = await host.AddUserAsync("bob");
        await host.SetEmailAsync(alice, "alice@example.com");
        await host.SetEmailAsync(bob, "bob@example.com");
        var groupId = await host.AddGroupAsync("family", alice, bob);
        var ruleId = await host.AddAccessRuleAsync("family", [Clause(AccessClauseKind.Group, groupId: groupId)]);
        await host.AttachAccessRuleAsync(route.Id, ruleId);

        var access = (await ResolveAsync(host, route.Id))[route.Id];

        Assert.Equal(["alice@example.com", "bob@example.com"], access.Emails);
    }

    [Fact]
    public async Task ADisabledAccount_ContributesNothing() {
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);
        var alice = await host.AddUserAsync("alice");
        await host.SetEmailAsync(alice, "alice@example.com");
        await host.DisableUserAsync(alice);
        var ruleId = await host.AddAccessRuleAsync("family", [Clause(AccessClauseKind.User, userId: alice)]);
        await host.AttachAccessRuleAsync(route.Id, ruleId);

        var access = (await ResolveAsync(host, route.Id))[route.Id];

        // Present but empty, so the projection locks the hostname out rather than falling back to the
        // instance-wide list — the operator attached a rule, and it now admits nobody.
        Assert.True(access.IsEmpty);
    }

    [Fact]
    public async Task AnAccountOfAnotherRealm_ContributesNothing() {
        // The realm invariant, carried across the seam (ADR-0040 decision 3). Reached by moving the route's
        // population out from under an existing attachment, which is the case a write cannot catch.
        using var host = AuthTestHost.Start();
        var otherRealmId = await host.AddRealmAsync("tenants");
        var templateId = await host.AddRealmTemplateAsync("tenant-apps", otherRealmId);
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated, templateId);

        // The account and the rule are the operator realm's; the route is now the tenant realm's.
        var operatorUser = await host.AddUserAsync("alice");
        await host.SetEmailAsync(operatorUser, "alice@example.com");
        var ruleId = await host.AddAccessRuleAsync("operators", [Clause(AccessClauseKind.User, userId: operatorUser)]);
        await host.AttachAccessRuleAsync(route.Id, ruleId);

        var access = (await ResolveAsync(host, route.Id))[route.Id];

        Assert.True(access.IsEmpty);
    }

    [Fact]
    public async Task AGroupMemberOfAnotherRealm_ContributesNothing() {
        // The same invariant one hop further out: a group clause must not be the way around it.
        using var host = AuthTestHost.Start();
        var otherRealmId = await host.AddRealmAsync("tenants");
        var templateId = await host.AddRealmTemplateAsync("tenant-apps", otherRealmId);
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated, templateId);

        var operatorUser = await host.AddUserAsync("alice");
        await host.SetEmailAsync(operatorUser, "alice@example.com");
        var groupId = await host.AddGroupAsync("operators", operatorUser);
        var ruleId = await host.AddAccessRuleAsync("operators", [Clause(AccessClauseKind.Group, groupId: groupId)]);
        await host.AttachAccessRuleAsync(route.Id, ruleId);

        var access = (await ResolveAsync(host, route.Id))[route.Id];

        Assert.True(access.IsEmpty);
    }

    [Fact]
    public async Task ClausesTheEdgeCannotEnforce_AreReportedRatherThanSilentlyDropped() {
        // Resolving *for* the in-process point: the external ids mean nothing there, and the caller is told
        // which kinds were skipped so it can warn instead of quietly narrowing the allow-list.
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);
        var ruleId = await host.AddAccessRuleAsync("family", [
            Clause(AccessClauseKind.ExternalPolicy, value: "pol-family"),
            Clause(AccessClauseKind.Email, value: "friend@elsewhere.com"),
        ]);
        await host.AttachAccessRuleAsync(route.Id, ruleId);

        var access = (await ResolveAsync(host, route.Id, AccessEnforcementPoint.InProcess))[route.Id];

        Assert.True(access.IsEmpty);
        Assert.Equal([AccessClauseKind.Email, AccessClauseKind.ExternalPolicy], access.UnsupportedKinds);
    }

    [Fact]
    public async Task TwoRules_ResolveInAttachmentOrder_AndDeduplicate() {
        // Order is precedence at the edge, so it survives; a policy named by both rules is attached once.
        using var host = AuthTestHost.Start();
        var route = await host.AddRouteAsync("app.example.invalid", AccessMode.Authenticated);
        var family = await host.AddAccessRuleAsync("family", [
            Clause(AccessClauseKind.ExternalPolicy, value: "pol-family"),
        ]);
        var friends = await host.AddAccessRuleAsync("friends", [
            Clause(AccessClauseKind.ExternalPolicy, value: "pol-friends"),
            Clause(AccessClauseKind.ExternalPolicy, value: "pol-family"),
        ]);
        await host.AttachAccessRuleAsync(route.Id, family, order: 0);
        await host.AttachAccessRuleAsync(route.Id, friends, order: 1);

        var access = (await ResolveAsync(host, route.Id))[route.Id];

        Assert.Equal(["family", "friends"], access.RuleNames);
        Assert.Equal(["pol-family", "pol-friends"], access.ExternalPolicyIds);
    }

    private static AccessRuleClause Clause(
        AccessClauseKind kind, int? userId = null, int? groupId = null, string? value = null) =>
        new() { Kind = kind, UserId = userId, GroupId = groupId, Value = value };

    private static async Task<IReadOnlyDictionary<int, AccessRuleResolver.ResolvedAccess>> ResolveAsync(
        AuthTestHost host,
        int routeId,
        AccessEnforcementPoint point = AccessEnforcementPoint.CloudflareAccess) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await AccessRuleResolver.ResolveAsync(db, [routeId], point, TestContext.Current.CancellationToken);
    }
}
