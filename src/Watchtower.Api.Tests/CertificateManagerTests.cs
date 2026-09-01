using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.InternalCa;
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
    /// issuance, so dropping out of the desired set keeps the row exactly where it is.
    /// </summary>
    [Fact]
    public async Task AHostLeavingTheDesiredSet_KeepsItsCertificate() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        await estate.Certificates.RenewNowAsync(Host, Ct);

        estate.Certificates.SetDesiredHosts([]);

        Assert.NotNull(estate.Store.Find(Host));
        Assert.NotNull(await estate.CertificateRowAsync(Host));
        // Still reported, flagged as unwanted — "why is this certificate still here" is the question the
        // list exists to answer.
        var state = estate.State(Host);
        Assert.False(state.Desired);
        Assert.Equal("active", state.State);
    }

    /// <summary>The one path that does delete: an operator removing the route has said the domain is gone.</summary>
    [Fact]
    public async Task ForgetHost_DeletesTheCertificateAndItsRow() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        await estate.Certificates.RenewNowAsync(Host, Ct);

        await estate.Certificates.ForgetHostAsync(Host, Ct);

        Assert.Null(estate.Store.Find(Host));
        Assert.Null(await estate.CertificateRowAsync(Host));
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

    /// <summary>
    /// Watchtower's own CA runs on this loop too (ADR-0033), and deliberately outside the issuer lease:
    /// that lease protects a rate-limited remote resource, while issuing here is local, free and
    /// row-race-guarded — and the instance an operator is talking to has to be able to make the port
    /// route they just created work rather than wait for whichever node holds a lease that exists for a
    /// different reason.
    /// </summary>
    [Fact]
    public async Task TheInternalCertificate_IsIssuedByANonHolderToo() {
        await using var estate = await AcmeEstate.StartAsync(
            settings: ("Watchtower:Proxy:PortRoutes:LanNames", "nas.lan, 192.168.1.10"));
        await estate.Factory.AddPortRouteAsync(9001, serviceName: "jellyfin", containerPort: 8096);
        estate.Factory.IssuerLease.IsHeld = false;
        estate.Factory.IssuerLease.CurrentHolder = "node-b:abc";
        estate.Ca.ForgetRequests();

        await estate.Certificates.ReconcileAsync(Ct);

        Assert.NotNull(estate.Store.Find(InternalCaNames.SharedLeafHost));
        // Local issuance, so nothing was asked of the CA — the lease's whole purpose is untouched.
        Assert.Empty(estate.Ca.Requests);
    }

    /// <summary>
    /// And the internal leaf's host never enters the ACME <em>desired</em> set. It is a store key, not a
    /// domain, and no public authority would issue for it — an order would be a refusal on a rate-limited
    /// endpoint, every pass. It is still listed, because it is held and served; what it is not is wanted.
    /// </summary>
    [Fact]
    public async Task TheInternalLeafsHost_IsHeldButNeverDesired() {
        await using var estate = await AcmeEstate.StartAsync(
            settings: ("Watchtower:Proxy:PortRoutes:LanNames", "nas.lan"));
        await estate.Factory.AddPortRouteAsync(9001, serviceName: "jellyfin", containerPort: 8096);
        await estate.AddRouteAsync(Host);

        await estate.Certificates.ReconcileAsync(Ct);

        var internalLeaf = estate.State(InternalCaNames.SharedLeafHost);
        Assert.False(internalLeaf.Desired);
        Assert.Contains("Watchtower Internal CA", internalLeaf.Issuer ?? "", StringComparison.Ordinal);
        // The routed domain is the only thing this instance would ever open an order for.
        Assert.Equal(
            [Host], estate.Certificates.Snapshot().Where(s => s.Desired).Select(s => s.Host).ToArray());
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
