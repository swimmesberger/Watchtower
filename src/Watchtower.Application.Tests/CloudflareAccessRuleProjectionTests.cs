using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// How attached access rules reach the edge (ADR-0039, ADR-0040): a route that attaches rules is projected
/// from <em>them</em> rather than from the instance-wide settings, and Watchtower owns that route's policy
/// attachments while leaving every other route's alone.
/// </summary>
/// <remarks>
/// The composition case is the one this whole feature exists for, so it is asserted directly: two rules on
/// one hostname attach two policies, in the operator's order, while a second hostname attaching one of them
/// gets only that one. Before rules, both hostnames necessarily got the same instance-wide list.
/// </remarks>
public sealed class CloudflareAccessRuleProjectionTests {
    private static Route NewRoute(int id, string domain, AccessMode mode = AccessMode.Authenticated) => new() {
        Id = id,
        Domain = domain,
        ServiceName = "web",
        ContainerPort = 80,
        AccessMode = mode,
    };

    private static AccessRuleResolver.ResolvedAccess Resolved(
        string[]? emails = null,
        string[]? emailDomains = null,
        string[]? externalGroupIds = null,
        string[]? externalPolicyIds = null,
        string[]? ruleNames = null,
        AccessClauseKind[]? unsupported = null) =>
        new(emails ?? [], emailDomains ?? [], externalGroupIds ?? [], externalPolicyIds ?? [],
            ruleNames ?? ["rule"], unsupported ?? []);

    /// <summary>The instance-wide settings a route with rules must ignore.</summary>
    private static readonly CloudflareProxyOptions Defaults = new() {
        AccessAllowedEmails = "everyone@example.com",
        AccessReusablePolicyIds = "pol-instance-wide",
    };

    [Fact]
    public void TwoRulesOnOneHostname_AttachBothPoliciesInOrder() {
        // The whole point of the feature: "the family policy and the friends policy" on one hostname, where
        // the second hostname keeps only the family one.
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "shared.example.com"), NewRoute(2, "internal.example.com")],
            new Dictionary<int, string[]>(),
            Defaults,
            new Dictionary<int, AccessRuleResolver.ResolvedAccess> {
                [1] = Resolved(externalPolicyIds: ["pol-family", "pol-friends"], ruleNames: ["family", "friends"]),
                [2] = Resolved(externalPolicyIds: ["pol-family"], ruleNames: ["family"]),
            });

        var shared = projection.Apps.Single(a => a.Domain == "shared.example.com");
        var internalApp = projection.Apps.Single(a => a.Domain == "internal.example.com");
        // Attachment order is preserved, not sorted: it is the precedence at the edge.
        Assert.Equal(["pol-family", "pol-friends"], shared.ReusablePolicyIds);
        Assert.Equal(["pol-family"], internalApp.ReusablePolicyIds);
        Assert.All(projection.Apps, app => Assert.True(app.OwnsPolicyAttachments));
        Assert.Empty(projection.Warnings);
    }

    [Fact]
    public void ARouteWithRules_IgnoresTheInstanceWideSettings() {
        // "Replace, not extend" (ADR-0039 decision 2): the operator stated who admits people, so the global
        // allow-list does not also apply — otherwise an internal hostname could never be narrower.
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com")],
            new Dictionary<int, string[]>(),
            Defaults,
            new Dictionary<int, AccessRuleResolver.ResolvedAccess> {
                [1] = Resolved(emails: ["friend@elsewhere.com"]),
            });

        var app = Assert.Single(projection.Apps);
        Assert.Equal(["friend@elsewhere.com"], app.Emails);
        Assert.Empty(app.ReusablePolicyIds);
    }

    [Fact]
    public void ARouteWithoutRules_KeepsTheInstanceWideSettings_AndDoesNotOwnItsAttachments() {
        // The no-op property every existing deployment relies on, and the reason the breaking half of
        // ADR-0040 is opt-in: nothing about this route's request changes.
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com")],
            new Dictionary<int, string[]>(),
            Defaults);

        var app = Assert.Single(projection.Apps);
        Assert.Equal(["everyone@example.com"], app.Emails);
        Assert.Equal(["pol-instance-wide"], app.ReusablePolicyIds);
        Assert.False(app.OwnsPolicyAttachments);
    }

    [Fact]
    public void ARuleThatResolvesToNobody_LocksTheRouteOut_AndNamesTheRule() {
        // The ADR-0035 rule, recomputed on the route's own clauses (ADR-0039 decision 5). Naming the rule is
        // the remedy: the operator looks in the rule, not in the settings page.
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com")],
            new Dictionary<int, string[]>(),
            Defaults,
            new Dictionary<int, AccessRuleResolver.ResolvedAccess> {
                [1] = Resolved(ruleNames: ["friends"]),
            });

        var app = Assert.Single(projection.Apps);
        Assert.True(app.IsLockout);
        // Deliberately not the instance-wide fallback: "attached a rule" and "wants the default" are
        // different statements, and guessing between them is how a hostname admits people nobody chose.
        Assert.Empty(app.Emails);
        Assert.Empty(app.ReusablePolicyIds);
        var warning = Assert.Single(projection.Warnings);
        Assert.Contains("friends", warning, StringComparison.Ordinal);
        Assert.Contains("denying everyone", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ALockout_AttachesNothing_SoNobodyElsesPolicyUndoesTheDeny() {
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com")],
            new Dictionary<int, string[]>(),
            Defaults,
            new Dictionary<int, AccessRuleResolver.ResolvedAccess> { [1] = Resolved() });

        var app = Assert.Single(projection.Apps);
        Assert.Equal(CloudflareTunnelProvider.AccessDecisionDeny, app.Decision);
    }

    [Fact]
    public void AClauseThisEdgeCannotEnforce_IsWarnedAbout_NotDroppedSilently() {
        // The divergence this feature exists to prevent. The write path refuses such an attachment, so
        // reaching here means the rule or the provider changed underneath it — which must still be said.
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com")],
            new Dictionary<int, string[]>(),
            Defaults,
            new Dictionary<int, AccessRuleResolver.ResolvedAccess> {
                [1] = Resolved(
                    externalPolicyIds: ["pol-family"],
                    ruleNames: ["family"],
                    unsupported: [AccessClauseKind.Group]),
            });

        var app = Assert.Single(projection.Apps);
        // The rest of the rule still applies — the warning is not a reason to lock the hostname out.
        Assert.Equal(["pol-family"], app.ReusablePolicyIds);
        Assert.False(app.IsLockout);
        var warning = Assert.Single(projection.Warnings);
        Assert.Contains("group clauses", warning, StringComparison.Ordinal);
        Assert.Contains("Cloudflare Access", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void ARestrictedRoute_IsProjectedFromItsGrants_EvenIfRulesAreSomehowResolved() {
        // Restricted means "exactly these subjects". setAccess refuses attachments on it, and the projection
        // does not consult them either — two independent guards on the same invariant.
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com", AccessMode.Restricted)],
            new Dictionary<int, string[]> { [1] = ["alice@example.com"] },
            Defaults,
            new Dictionary<int, AccessRuleResolver.ResolvedAccess> {
                [1] = Resolved(externalPolicyIds: ["pol-friends"]),
            });

        var app = Assert.Single(projection.Apps);
        Assert.Equal(["alice@example.com"], app.Emails);
        Assert.Empty(app.ReusablePolicyIds);
        Assert.False(app.OwnsPolicyAttachments);
    }

    [Fact]
    public void ExternalGroupClauses_BecomeAccessGroupIncludes() {
        // The per-route spelling of the instance-wide AccessGroupIds setting.
        var projection = CloudflareTunnelProvider.ProjectAccessApps(
            [NewRoute(1, "app.example.com")],
            new Dictionary<int, string[]>(),
            Defaults,
            new Dictionary<int, AccessRuleResolver.ResolvedAccess> {
                [1] = Resolved(externalGroupIds: ["grp-entra"]),
            });

        var app = Assert.Single(projection.Apps);
        Assert.Equal(["grp-entra"], app.GroupIds);
        Assert.False(app.IsLockout);
    }
}
