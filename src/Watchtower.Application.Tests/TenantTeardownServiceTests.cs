using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="TenantTeardownService"/> — the one code path behind both
/// <c>templates.removeTenant</c> and <c>DELETE /api/mgmt/templates/{id}/tenants/{slug}</c>.
/// </summary>
/// <remarks>
/// Teardown is ordered, and the order is what these tests pin: refuse under an in-flight deploy, bring
/// the compose project down <em>before</em> the row that names it is deleted, abort the whole thing if
/// that fails, and reload the proxy only once the routes have actually cascaded away.
/// </remarks>
public sealed class TenantTeardownServiceTests {
    [Fact]
    public async Task Teardown_BringsTheProjectDown_DeletesTheStackWithItsChildren_ThenReloadsTheProxy() {
        using var host = AuthTestHost.Start();
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await SeedTenantAsync(host, templateId, "acme");

        var (result, compose, proxy) = await TeardownAsync(host, templateId, "acme", probeStackId: stackId);

        Assert.Equal(TenantTeardownStatus.Removed, result.Status);
        Assert.Equal([("billing-acme", false)], compose.Downs);
        Assert.Equal(1, proxy.ApplyCount);

        // The order, not just the outcome. Compose was asked to bring the project down while the row
        // that names that project still existed, and the proxy was reloaded only once it was gone —
        // deleting first would strand containers nothing names, reloading first would drop the route
        // while it was still being served.
        Assert.True(compose.StackExistedAtDown);
        Assert.False(proxy.StackExistedAtApply);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;
        Assert.False(await db.Stacks.AnyAsync(s => s.Id == stackId, ct));
        // Nothing deleted these by hand — the FK cascades did, which is what teardown relies on.
        Assert.False(await db.Routes.AnyAsync(r => r.StackId == stackId, ct));
        Assert.False(await db.StackEnvVars.AnyAsync(v => v.StackId == stackId, ct));
        Assert.False(await db.DeployEvents.AnyAsync(e => e.StackId == stackId, ct));
    }

    [Fact]
    public async Task Teardown_PassesTheVolumePurgeThroughToCompose() {
        using var host = AuthTestHost.Start();
        var templateId = await host.AddTemplateAsync("billing");
        await SeedTenantAsync(host, templateId, "acme");

        var (result, compose, _) = await TeardownAsync(host, templateId, "acme", removeVolumes: true);

        Assert.Equal(TenantTeardownStatus.Removed, result.Status);
        Assert.Equal([("billing-acme", true)], compose.Downs);
    }

    /// <summary>
    /// A failed compose-down leaves containers up and the row intact: a state the caller can simply
    /// retry. Deleting the row anyway would strand the containers with nothing left naming them.
    /// </summary>
    [Fact]
    public async Task Teardown_AbortsBeforeDeletingAnythingWhenComposeDownFails() {
        using var host = AuthTestHost.Start();
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await SeedTenantAsync(host, templateId, "acme");

        var (result, compose, proxy) = await TeardownAsync(
            host, templateId, "acme", downExitCode: 1, probeStackId: stackId);

        Assert.Equal(TenantTeardownStatus.ComposeDownFailed, result.Status);
        Assert.Single(compose.Downs);
        // The row was still there when compose was called, and nothing after it ran.
        Assert.True(compose.StackExistedAtDown);
        Assert.Equal(0, proxy.ApplyCount);
        Assert.Null(proxy.StackExistedAtApply);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var ct = TestContext.Current.CancellationToken;
        Assert.True(await db.Stacks.AnyAsync(s => s.Id == stackId, ct));
        Assert.True(await db.Routes.AnyAsync(r => r.StackId == stackId, ct));
        // Whatever went wrong stays in the operator's log; the message never carries command output.
        Assert.DoesNotContain("stubbed compose down", result.Error);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task Teardown_RefusesUnderAnInFlightDeploy_WithoutTouchingCompose(string status) {
        using var host = AuthTestHost.Start();
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await SeedTenantAsync(host, templateId, "acme");
        await host.AddDeployEventAsync(stackId, status);

        var (result, compose, proxy) = await TeardownAsync(host, templateId, "acme");

        Assert.Equal(TenantTeardownStatus.DeployActive, result.Status);
        Assert.Empty(compose.Downs);
        Assert.Equal(0, proxy.ApplyCount);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.True(await db.Stacks.AnyAsync(s => s.Id == stackId, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("success")]
    [InlineData("failed")]
    public async Task Teardown_IsNotBlockedByAFinishedDeploy(string status) {
        using var host = AuthTestHost.Start();
        var templateId = await host.AddTemplateAsync("billing");
        var stackId = await SeedTenantAsync(host, templateId, "acme");
        await host.AddDeployEventAsync(stackId, status);

        var (result, _, _) = await TeardownAsync(host, templateId, "acme");

        Assert.Equal(TenantTeardownStatus.Removed, result.Status);
    }

    [Theory]
    [InlineData("nosuchtenant")]
    [InlineData("")]
    [InlineData("not a slug")]
    public async Task Teardown_ReportsAnUnknownOrUnusableSlugAsNotFound(string slug) {
        using var host = AuthTestHost.Start();
        var templateId = await host.AddTemplateAsync("billing");
        await SeedTenantAsync(host, templateId, "acme");

        var (result, compose, _) = await TeardownAsync(host, templateId, slug);

        Assert.Equal(TenantTeardownStatus.TenantNotFound, result.Status);
        Assert.Empty(compose.Downs);
    }

    /// <summary>
    /// The lookup is always constrained by the template, so a slug that names a tenant of a
    /// <em>different</em> template is not found rather than reachable.
    /// </summary>
    [Fact]
    public async Task Teardown_WillNotReachATenantOfAnotherTemplate() {
        using var host = AuthTestHost.Start();
        var billing = await host.AddTemplateAsync("billing");
        var portal = await host.AddTemplateAsync("portal", "{tenant}.portal.example.com");
        var portalTenant = await SeedTenantAsync(host, portal, "acme", "portal-acme", "acme.portal.example.com");

        var (result, compose, _) = await TeardownAsync(host, billing, "acme");

        Assert.Equal(TenantTeardownStatus.TenantNotFound, result.Status);
        Assert.Empty(compose.Downs);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.True(await db.Stacks.AnyAsync(s => s.Id == portalTenant, TestContext.Current.CancellationToken));
    }

    // -- Helpers ---------------------------------------------------------------------------------

    /// <summary>Seeds a tenant with the children teardown has to cascade: a route, an env var, a deploy event.</summary>
    private static async Task<int> SeedTenantAsync(
        AuthTestHost host, int templateId, string slug,
        string? stackName = null, string? domain = null) {
        var name = stackName ?? $"billing-{slug}";
        var stackId = await host.AddStackAsync(name, templateId, slug, composeProjectName: name);
        await host.AddRouteAsync(stackId, domain ?? $"{slug}.example.com");

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.StackEnvVars.Add(new StackEnvVar { StackId = stackId, Key = "SECRET", Value = "value" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return stackId;
    }

    /// <summary>
    /// Runs a teardown against doubles for its two out-of-process steps. With
    /// <paramref name="probeStackId"/> both doubles sample whether that stack row still exists at the
    /// moment they are called, which is what turns the ordering from an implication of the end state
    /// into something a test can actually fail on.
    /// </summary>
    private static async Task<(TenantTeardownResult Result, StubComposeCliService Compose, RecordingProxyProvider Proxy)>
        TeardownAsync(
            AuthTestHost host, int templateId, string? slug, bool removeVolumes = false, int downExitCode = 0,
            int? probeStackId = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var compose = new StubComposeCliService { DownExitCode = downExitCode };
        var proxy = new RecordingProxyProvider();

        if (probeStackId is { } id) {
            compose.StackProbe = Probe;
            proxy.StackProbe = Probe;

            bool Probe() {
                // A scope of its own: the teardown's context is mid-call, and this must read committed
                // state rather than whatever that context happens to be tracking.
                using var probeScope = host.Services.CreateScope();
                return probeScope.ServiceProvider.GetRequiredService<WatchtowerDbContext>()
                    .Stacks.AsNoTracking().Any(s => s.Id == id);
            }
        }

        var service = new TenantTeardownService(
            scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>(),
            compose, proxy, NullLogger<TenantTeardownService>.Instance);

        var result = await service.TeardownAsync(
            templateId, slug, removeVolumes, TestContext.Current.CancellationToken);
        return (result, compose, proxy);
    }
}
