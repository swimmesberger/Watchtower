using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The scheduling half of certificate management: which hosts are wanted, when an attempt is made, and
/// — most of all — when files are and are not deleted. ADR-0022.
/// </summary>
/// <remarks>
/// The asymmetry these tests exist to protect: a host leaving the desired set costs nothing to get wrong
/// in one direction and a fresh issuance against a rate limit in the other. So the desired set is never
/// a delete trigger, and only the route-delete path removes anything.
/// </remarks>
public sealed class CertificateManagerTests {
    private const string Host = "app.example.invalid";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A domain no CA would issue for is dropped rather than thrown over: the desired set is computed
    /// from the route projection, which must not break because one row is bad.
    /// </summary>
    [Fact]
    public async Task SetDesiredHosts_DropsWhatCannotBeIssuedFor() {
        await using var estate = await AcmeEstate.StartAsync();

        estate.Certificates.SetDesiredHosts([Host, "*.example.invalid", "not a host", "  APP2.Example.Invalid  "]);

        var hosts = estate.Certificates.Snapshot().Select(s => s.Host).ToArray();
        Assert.Equal([Host, "app2.example.invalid"], hosts);
    }

    /// <summary>
    /// The contract that matters most. A route removed by mistake and put back must not have cost an
    /// issuance, so dropping out of the desired set keeps the files exactly where they are.
    /// </summary>
    [Fact]
    public async Task AHostLeavingTheDesiredSet_KeepsItsCertificate() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        await estate.Certificates.RenewNowAsync(Host, Ct);

        estate.Certificates.SetDesiredHosts([]);

        Assert.NotNull(estate.Store.Find(Host));
        Assert.True(Directory.Exists(Path.Combine(estate.Store.RootPath, Host)));
        // Still reported, flagged as unwanted — "why is this certificate still here" is the question the
        // list exists to answer.
        var state = estate.State(Host);
        Assert.False(state.Desired);
        Assert.Equal("active", state.State);
    }

    /// <summary>The one path that does delete: an operator removing the route has said the domain is gone.</summary>
    [Fact]
    public async Task ForgetHost_DeletesTheCertificateAndItsFiles() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        await estate.Certificates.RenewNowAsync(Host, Ct);

        await estate.Certificates.ForgetHostAsync(Host, Ct);

        Assert.Null(estate.Store.Find(Host));
        Assert.False(Directory.Exists(Path.Combine(estate.Store.RootPath, Host)));
        Assert.Null(estate.Store.SelectContext(Host));
    }

    [Fact]
    public async Task ForgetHost_RefusesANameThatCouldNeverHaveBeenStored() {
        await using var estate = await AcmeEstate.StartAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => estate.Certificates.ForgetHostAsync("../etc", Ct));
    }

    /// <summary>
    /// The provider is switchable at runtime, so the loop stays alive while another one is selected — but
    /// it must not spend a single request at the CA while it is.
    /// </summary>
    [Fact]
    public async Task WhileAnotherProviderIsSelected_NothingIsOrdered() {
        await using var estate = await AcmeEstate.StartAsync(
            settings: ("Watchtower:Proxy:Provider", "caddy"));
        estate.Certificates.SetDesiredHosts([Host]);

        await estate.Certificates.ReconcileAsync(Ct);

        Assert.Empty(estate.Ca.Requests);
        Assert.Null(estate.Store.Find(Host));
    }

    [Fact]
    public async Task WhileTheProxyIsDisabled_NothingIsOrdered() {
        await using var estate = await AcmeEstate.StartAsync(
            settings: ("Watchtower:Proxy:Enabled", "false"));
        estate.Certificates.SetDesiredHosts([Host]);

        await estate.Certificates.ReconcileAsync(Ct);

        Assert.Empty(estate.Ca.Requests);
    }

    /// <summary>
    /// No HTTPS listener means a certificate could not be served even if it were issued, so the loop does
    /// not order one — it would spend rate limit to produce something invisible.
    /// </summary>
    [Fact]
    public async Task WithoutAnHttpsListener_TheLoopDoesNotOrder() {
        await using var estate = await AcmeEstate.StartAsync(httpsBound: false);
        await estate.AddRouteAsync(Host);

        await estate.Certificates.ReconcileAsync(Ct);

        Assert.Empty(estate.Ca.Requests);
    }

    /// <summary>
    /// …but an operator asking outright is not the loop. "Renew now" is an explicit action, and it is also
    /// the path a Pebble or step-ca run drives before any listener exists.
    /// </summary>
    [Fact]
    public async Task WithoutAnHttpsListener_RenewNowStillOrders() {
        await using var estate = await AcmeEstate.StartAsync(httpsBound: false);
        await estate.AddRouteAsync(Host);

        var outcome = await estate.Certificates.RenewNowAsync(Host, Ct);

        Assert.IsType<IssueOutcome.Issued>(outcome);
    }

    /// <summary>
    /// A login host needs a certificate like any other host, and since ADR-0023 it gets one for the
    /// ordinary reason: it is a <c>Watchtower</c>-target route in the table, so the route projection puts
    /// it in the desired set. This one arrives by the upgrade path — the host boots with a configured
    /// <c>Auth:Host</c> and <c>LoginHostConversion</c> turns it into the operator realm's login route
    /// before the providers start.
    /// </summary>
    [Fact]
    public async Task AConvertedLoginHostIsWanted() {
        await using var estate = await AcmeEstate.StartAsync(
            settings: [("Watchtower:Auth:Enabled", "true"), ("Watchtower:Auth:Host", "login.example.invalid")]);

        await estate.AddRouteAsync(Host);

        var login = Assert.Single(estate.Certificates.Snapshot(), s => s.Host == "login.example.invalid");
        Assert.True(login.Desired);

        // And it is wanted because a row says so, not because configuration was read a second time.
        await estate.Factory.WithScopeAsync(async sp => {
            var db = sp.GetRequiredService<WatchtowerDbContext>();
            var route = await db.Routes.AsNoTracking()
                .SingleAsync(r => r.Domain == "login.example.invalid", Ct);
            Assert.Equal(RouteTarget.Watchtower, route.Target);
            var system = await db.Realms.AsNoTracking().SingleAsync(r => r.IsSystem, Ct);
            Assert.Equal(route.Id, system.LoginRouteId);
        });
    }

    /// <summary>
    /// And the list says which is which. Every served host has a route row now (ADR-0023), so the only
    /// other thing the list can show is a leftover on the volume: a certificate nothing routes to.
    /// </summary>
    [Fact]
    public async Task TheListDistinguishesRoutedHostsFromLeftovers() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        await estate.Certificates.RenewNowAsync(Host, Ct);

        Assert.Equal("route", Assert.Single(await estate.ListCertificatesAsync(), c => c.Host == Host).Source);

        // A certificate for a host that is then dropped from the desired set and has no route row behind
        // it: on disk, wanted by nothing. Left there deliberately (dropping out of the set is not a
        // delete) and flagged so an operator can see why it is still around.
        estate.Certificates.SetDesiredHosts([Host, "gone.example.invalid"]);
        await estate.Certificates.RenewNowAsync("gone.example.invalid", Ct);
        estate.Certificates.SetDesiredHosts([Host]);

        var orphan = Assert.Single(await estate.ListCertificatesAsync(), c => c.Host == "gone.example.invalid");
        Assert.Equal("orphan", orphan.Source);
        Assert.Null(orphan.RouteId);
    }

    /// <summary>
    /// Two callers for one host join the same attempt. Without that, a nudge racing the loop — or an
    /// operator pressing the button twice — opens two orders for one name against the CA's limit on
    /// pending authorizations.
    /// </summary>
    [Fact]
    public async Task ConcurrentAttemptsForOneHost_ShareOneOrder() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);

        await Task.WhenAll(
            estate.Certificates.RenewNowAsync(Host, Ct),
            estate.Certificates.RenewNowAsync(Host, Ct),
            estate.Certificates.RenewNowAsync(Host, Ct));

        Assert.Equal(1, estate.Ca.Requests.Count(p => p == "/new-order"));
    }

    /// <summary>
    /// The reconcile is driven by the desired set, and a host that is merely on disk is not part of it.
    /// Otherwise deleting a route would leave the deployment renewing its certificate forever.
    /// </summary>
    [Fact]
    public async Task AnUndesiredHostIsNotRenewed() {
        await using var estate = await AcmeEstate.StartAsync();
        estate.Ca.CertificateAge = TimeSpan.FromDays(80);
        await estate.AddRouteAsync(Host);
        await estate.Certificates.RenewNowAsync(Host, Ct);

        estate.Certificates.SetDesiredHosts([]);
        var before = estate.Ca.Requests.Count;
        await estate.Certificates.ReconcileAsync(Ct);

        Assert.Equal(before, estate.Ca.Requests.Count);
    }

    /// <summary>
    /// The status line the Settings page shows while a first start works through its routes. Null once
    /// everything is issued, so a healthy deployment gets no caveat at all.
    /// </summary>
    [Fact]
    public async Task TheProxyStatusReportsIssuanceProgress() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        await estate.AddRouteAsync("other.example.invalid");

        await estate.Certificates.RenewNowAsync(Host, Ct);
        Assert.Equal("1 of 2 certificates issued", await ProviderDetailAsync(estate));

        await estate.Certificates.RenewNowAsync("other.example.invalid", Ct);
        Assert.Null(await ProviderDetailAsync(estate));
    }

    private static async Task<string?> ProviderDetailAsync(AcmeEstate estate) {
        string? detail = null;
        await estate.Factory.WithScopeAsync(async sp => {
            var result = await sp
                .GetRequiredService<Elarion.Abstractions.IHandler<
                    Application.Modules.Proxy.Handlers.GetProxyStatus.Query,
                    Elarion.Abstractions.Result<Application.Modules.Proxy.Handlers.GetProxyStatus.Response>>>()
                .HandleAsync(new Application.Modules.Proxy.Handlers.GetProxyStatus.Query(), Ct);
            detail = result.Value.ProviderDetail;
        });
        return detail;
    }
}
