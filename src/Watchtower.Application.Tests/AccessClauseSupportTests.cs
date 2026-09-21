using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The portability table (ADR-0039 decision 3). Pinned as its own test because it is the thing that turns a
/// shared clause vocabulary into an honest one: every refusal message, every greyed-out row in the UI and
/// the projection's own warnings read the answer from here, so a kind silently gaining an enforcement point
/// would widen an allow-list nobody re-checked.
/// </summary>
public sealed class AccessClauseSupportTests {
    [Fact]
    public void SubjectsWatchtowerOwns_AreEnforceableEverywhere() {
        Assert.Equal(AccessEnforcementPoint.Everywhere, AccessClauseSupport.SupportedBy(AccessClauseKind.User));
        Assert.Equal(AccessEnforcementPoint.Everywhere, AccessClauseSupport.SupportedBy(AccessClauseKind.Group));
    }

    [Theory]
    [InlineData(AccessClauseKind.Email)]
    [InlineData(AccessClauseKind.EmailDomain)]
    [InlineData(AccessClauseKind.ExternalGroup)]
    [InlineData(AccessClauseKind.ExternalPolicy)]
    public void EdgeOnlyKinds_AreNotEnforceableInProcess(AccessClauseKind kind) {
        // The two email kinds because Watchtower does not verify or uniquely constrain User.Email, and the
        // two external kinds because they are opaque ids. Different reasons, same answer.
        Assert.Equal(AccessEnforcementPoint.CloudflareAccess, AccessClauseSupport.SupportedBy(kind));
        Assert.False(AccessClauseSupport.IsSupportedAt(kind, AccessEnforcementPoint.InProcess));
        Assert.True(AccessClauseSupport.IsSupportedAt(kind, AccessEnforcementPoint.CloudflareAccess));
    }

    [Fact]
    public void AnUnknownKind_IsEnforceableNowhere() {
        // Fail-closed: a value from a newer build must not be read as "everyone".
        Assert.Equal(AccessEnforcementPoint.None, AccessClauseSupport.SupportedBy((AccessClauseKind)999));
        Assert.False(AccessClauseSupport.IsSupportedAt((AccessClauseKind)999, AccessEnforcementPoint.CloudflareAccess));
    }

    [Fact]
    public void ARulesPortability_IsTheIntersectionOfItsClauses() {
        // One in-process-only clause among portable ones is what decides the whole rule, because an edge that
        // drops a clause admits fewer people than the rule says.
        Assert.Equal(
            AccessEnforcementPoint.CloudflareAccess,
            AccessClauseSupport.SupportedByAll([AccessClauseKind.Group, AccessClauseKind.ExternalPolicy]));
        Assert.Equal(
            AccessEnforcementPoint.Everywhere,
            AccessClauseSupport.SupportedByAll([AccessClauseKind.Group, AccessClauseKind.User]));
    }

    [Fact]
    public void AnEmptyRule_IsEnforceableEverywhere() {
        // It admits nobody, and every point can represent that faithfully. Whether a route may attach it is
        // the lockout question, not a portability one.
        Assert.Equal(AccessEnforcementPoint.Everywhere, AccessClauseSupport.SupportedByAll([]));
    }

    [Fact]
    public void OnlyTheCloudflareProvider_DecidesAtTheEdge() {
        Assert.Equal(
            AccessEnforcementPoint.CloudflareAccess,
            AccessClauseSupport.PointFor(ProxyProviderKind.Cloudflare));
        // Both of the others emit a forward-auth hop back to Watchtower, so both decide in process.
        Assert.Equal(AccessEnforcementPoint.InProcess, AccessClauseSupport.PointFor(ProxyProviderKind.Yarp));
        Assert.Equal(AccessEnforcementPoint.InProcess, AccessClauseSupport.PointFor(ProxyProviderKind.Caddy));
    }

    [Fact]
    public void SeveralKinds_AreNamedAsOnePhrase() {
        // The refusal messages embed this, so the grammar is worth pinning: an operator reads it, not a log
        // parser.
        Assert.Equal(
            "email and external policy clauses",
            AccessClauseSupport.DescribeAll([AccessClauseKind.Email, AccessClauseKind.ExternalPolicy]));
        Assert.Equal("group clauses", AccessClauseSupport.DescribeAll([AccessClauseKind.Group]));
        Assert.Equal("no clauses", AccessClauseSupport.DescribeAll([]));
    }
}
