using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the certificate bookkeeping the in-process proxy writes onto the route rows. It is what the
/// Routes page shows, so the interesting cases are the ones where writing the obvious thing would be
/// wrong: a failed renewal must not un-issue the certificate still being served, and a reconcile must
/// not knock a working route back to "pending" just because it swept past it.
/// </summary>
public sealed class RouteStatusUpdaterTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RecordIssued_MarksTheRouteActiveWithItsExpiry_AndClearsTheDetail() {
        using var host = AuthTestHost.Start();
        var stackId = await host.AddStackAsync("billing");
        await host.AddRouteAsync(stackId, "app.example.invalid");
        await SetStatusAsync(host, "app.example.invalid", RouteStatus.Error, "the CA said no");
        var notAfter = DateTimeOffset.UtcNow.AddDays(90);

        await Updater(host).RecordIssuedAsync("App.Example.Invalid", notAfter, Ct);

        var route = await LoadAsync(host, "app.example.invalid");
        Assert.Equal(RouteStatus.Active, route.Status);
        // The previous failure is history the moment a certificate exists; leaving it would read as a
        // route that is both serving and broken.
        Assert.Null(route.StatusDetail);
        Assert.Equal(notAfter.ToUnixTimeSeconds(), route.CertNotAfter!.Value.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData(RouteStatus.Error)]
    [InlineData(RouteStatus.AwaitingDns)]
    public async Task RecordFailed_RecordsTheReason_AndKeepsTheExistingExpiry(RouteStatus status) {
        using var host = AuthTestHost.Start();
        var stackId = await host.AddStackAsync("billing");
        await host.AddRouteAsync(stackId, "app.example.invalid");
        var issuedUntil = DateTimeOffset.UtcNow.AddDays(20);
        await Updater(host).RecordIssuedAsync("app.example.invalid", issuedUntil, Ct);

        await Updater(host).RecordFailedAsync("app.example.invalid", status, "rate limited", Ct);

        var route = await LoadAsync(host, "app.example.invalid");
        Assert.Equal(status, route.Status);
        Assert.Equal("rate limited", route.StatusDetail);
        // A renewal that failed has not taken the certificate currently being served away.
        Assert.Equal(issuedUntil.ToUnixTimeSeconds(), route.CertNotAfter!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task RecordFailed_CapsTheDetail() {
        using var host = AuthTestHost.Start();
        var stackId = await host.AddStackAsync("billing");
        await host.AddRouteAsync(stackId, "app.example.invalid");

        // A CA that answers with an HTML error page must not put a novel in a table column.
        await Updater(host).RecordFailedAsync("app.example.invalid", RouteStatus.Error, new string('x', 900), Ct);

        var route = await LoadAsync(host, "app.example.invalid");
        Assert.Equal(500, route.StatusDetail!.Length);
    }

    [Fact]
    public async Task RecordFailed_RefusesAStatusThatIsNotAFailure() {
        using var host = AuthTestHost.Start();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Updater(host).RecordFailedAsync("app.example.invalid", RouteStatus.Active, "…", Ct));
    }

    [Fact]
    public async Task MarkPending_TouchesOnlyFreshlyCreatedRows() {
        using var host = AuthTestHost.Start();
        var stackId = await host.AddStackAsync("billing");
        await host.AddRouteAsync(stackId, "fresh.example.invalid");
        await host.AddRouteAsync(stackId, "serving.example.invalid");
        await host.AddRouteAsync(stackId, "explained.example.invalid");
        await host.AddRouteAsync(stackId, "unlisted.example.invalid");
        await SetStatusAsync(host, "serving.example.invalid", RouteStatus.Active, null);
        await SetStatusAsync(host, "explained.example.invalid", RouteStatus.Pending, "waiting on the DNS check");

        await Updater(host).MarkPendingAsync(
            ["Fresh.Example.Invalid", "serving.example.invalid", "explained.example.invalid"], Ct);

        Assert.Equal("Waiting for a certificate", (await LoadAsync(host, "fresh.example.invalid")).StatusDetail);
        // Already serving: a sweep past it must not report it as waiting for what it already has.
        var serving = await LoadAsync(host, "serving.example.invalid");
        Assert.Equal(RouteStatus.Active, serving.Status);
        Assert.Null(serving.StatusDetail);
        // A specific explanation beats this generic one.
        Assert.Equal("waiting on the DNS check", (await LoadAsync(host, "explained.example.invalid")).StatusDetail);
        Assert.Null((await LoadAsync(host, "unlisted.example.invalid")).StatusDetail);
    }

    [Fact]
    public async Task AWriteForADomainNobodyRoutes_IsSilentlyIgnored() {
        using var host = AuthTestHost.Start();
        // Bookkeeping is never allowed to fail the certificate work it reports on.
        await Updater(host).RecordIssuedAsync("gone.example.invalid", DateTimeOffset.UtcNow, Ct);
        await Updater(host).MarkPendingAsync(["gone.example.invalid"], Ct);
        await Updater(host).MarkPendingAsync([], Ct);
    }

    private static RouteStatusUpdater Updater(AuthTestHost host) =>
        host.Services.GetRequiredService<RouteStatusUpdater>();

    private static async Task<Route> LoadAsync(AuthTestHost host, string domain) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Routes.AsNoTracking().SingleAsync(r => r.Domain == domain, Ct);
    }

    private static async Task SetStatusAsync(AuthTestHost host, string domain, RouteStatus status, string? detail) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Routes.Where(r => r.Domain == domain)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.StatusDetail, detail), Ct);
    }
}
