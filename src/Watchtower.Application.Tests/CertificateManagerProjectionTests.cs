using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// What the certificate manager writes onto a route row for a certificate it did <em>not</em> issue
/// itself. Only a successful ACME issuance used to mark a row Active, so a certificate an operator had
/// hand-placed in the volume — or one issued before the last restart — was served perfectly while the
/// Routes page said "Waiting for a certificate" forever, next to a certificates list that reported the
/// very same host as active.
/// </summary>
public sealed class CertificateManagerProjectionTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Host = "app.example.invalid";

    [Fact]
    public async Task Reconcile_MarksARouteActive_ForACertificateItDidNotIssue() {
        using var host = StartHost();
        await SeedRouteAsync(host);
        using var chain = TestCertificates.Create(Host);
        var store = host.Services.GetRequiredService<CertificateStore>();
        // Placed the way an operator places one: straight into the store, with no order behind it.
        await store.InstallAsync(Host, chain.PemChain, chain.Key!, Ct);

        var manager = host.Services.GetRequiredService<CertificateManager>();
        manager.SetDesiredHosts([Host]);
        // The HTTPS listener is unbound, so the pass makes no ACME attempt at all — the row can only have
        // been written by the projection of what the store already holds.
        await manager.ReconcileAsync(Ct);

        var route = await LoadAsync(host);
        Assert.Equal(RouteStatus.Active, route.Status);
        Assert.Null(route.StatusDetail);
        Assert.NotNull(route.CertNotAfter);
        Assert.Equal(chain.Leaf.NotAfter.ToUniversalTime(), route.CertNotAfter!.Value);
    }

    [Fact]
    public async Task Reconcile_DoesNotRewriteTheRow_OnEveryPass() {
        using var host = StartHost();
        await SeedRouteAsync(host);
        using var chain = TestCertificates.Create(Host);
        await host.Services.GetRequiredService<CertificateStore>()
            .InstallAsync(Host, chain.PemChain, chain.Key!, Ct);

        var manager = host.Services.GetRequiredService<CertificateManager>();
        manager.SetDesiredHosts([Host]);
        await manager.ReconcileAsync(Ct);

        // A marker only a second write would clear: RecordIssuedAsync sets StatusDetail back to null.
        // The loop runs this pass every five minutes for the life of the process, so a projection that
        // fired each time would churn the row — and anything watching it — for no change at all.
        await StampAsync(host, "left alone");
        await manager.ReconcileAsync(Ct);

        var route = await LoadAsync(host);
        Assert.Equal("left alone", route.StatusDetail);
        Assert.Equal(RouteStatus.Active, route.Status);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>The proxy enabled and the in-process provider selected — the manager no-ops otherwise.</summary>
    private static AuthTestHost StartHost() => AuthTestHost.Start(
        ("Watchtower:Proxy:Enabled", "true"),
        ("Watchtower:Proxy:Provider", "yarp"));

    private static async Task SeedRouteAsync(AuthTestHost host) {
        var stackId = await host.AddStackAsync("billing", composeProjectName: "billing");
        await host.AddRouteAsync(stackId, Host);
    }

    private static async Task<Route> LoadAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Routes.AsNoTracking().SingleAsync(r => r.Domain == Host, Ct);
    }

    private static async Task StampAsync(AuthTestHost host, string detail) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Routes.Where(r => r.Domain == Host)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.StatusDetail, detail), Ct);
    }
}
