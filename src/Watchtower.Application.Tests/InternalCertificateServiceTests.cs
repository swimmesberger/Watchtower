using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.InternalCa;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// When the shared LAN certificate is issued and when it is left alone. The decision has to be stable —
/// this runs on every start and every route change — and it has to notice the three things that make the
/// held certificate wrong: the names moved, the CA moved, or it is old.
/// </summary>
/// <remarks>
/// Most of these pass the "is a leaf wanted" question in, so the issue/reissue/install/prune decisions
/// can be exercised without a route table. The production predicate — "is there a port-bound route?" —
/// and the route statuses it settles have their own section at the end.
/// </remarks>
public sealed class InternalCertificateServiceTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Host = InternalCaNames.SharedLeafHost;

    /// <summary>A non-empty port-route list, which is what "something wants a leaf" means.</summary>
    private static readonly ValueTask<IReadOnlyList<int>> Wanted =
        ValueTask.FromResult<IReadOnlyList<int>>([9999]);

    [Fact]
    public async Task TheFirstPass_IssuesALeafFromTheInternalCa() {
        using var host = LanHost("nas.lan, 192.168.1.10");

        await EnsureAsync(host);

        var leaf = Store(host).SelectCertificate(Host);
        Assert.NotNull(leaf);
        Assert.Equal("CN=Watchtower Internal CA", leaf.Issuer);

        var row = await RowAsync(host);
        Assert.NotNull(row);
        // The source is what keeps it out of the ACME desired set and out of the prune.
        Assert.Equal(ProxyCertificateSources.Internal, row.Source);
        Assert.Equal(leaf.Thumbprint, row.Thumbprint);

        // And the CA itself is a row now, so the next start — or another instance — signs with the same
        // root rather than one the operator has never imported.
        var ca = await CaRowAsync(host);
        Assert.NotNull(ca);
        Assert.Equal(InternalCaNames.CaRowName, ca.Name);
        Assert.NotEmpty(ca.PrivateKey);
    }

    [Fact]
    public async Task ASecondPassWithTheSameInputs_ChangesNothing() {
        using var host = LanHost("nas.lan");
        await EnsureAsync(host);
        var first = Store(host).SelectCertificate(Host)!.Thumbprint;

        await EnsureAsync(host);
        await EnsureAsync(host);

        // Reissuing on every pass would churn the row, wake every other instance through the change
        // signal, and replace a perfectly good certificate for nothing.
        Assert.Equal(first, Store(host).SelectCertificate(Host)!.Thumbprint);
    }

    /// <summary>
    /// The address form that has no place in a certificate. A scope id survives neither the SAN
    /// encoding nor a read-back, so if the configured value kept one, every pass would compare a scoped
    /// address against an unscoped one, conclude the names had changed, and reissue forever.
    /// </summary>
    [Fact]
    public async Task AScopedIpv6Address_SettlesAfterOnePass() {
        using var host = LanHost("fe80::1%3, nas.lan");
        await EnsureAsync(host);
        var first = Store(host).SelectCertificate(Host)!.Thumbprint;

        await EnsureAsync(host);
        await EnsureAsync(host);

        Assert.Equal(first, Store(host).SelectCertificate(Host)!.Thumbprint);
        Assert.Contains("fe80::1", await NamesAsync(host));
    }

    [Fact]
    public async Task AChangedLanName_IsReissued() {
        using var host = LanHost("nas.lan");
        await EnsureAsync(host);
        var before = Store(host).SelectCertificate(Host)!.Thumbprint;

        // The next start after an operator added the machine's address to the field.
        using var restarted = host.Restart(LanSettings("nas.lan, 192.168.1.10"));
        await EnsureAsync(restarted);

        var after = Store(restarted).SelectCertificate(Host)!;
        Assert.NotEqual(before, after.Thumbprint);
        var names = await NamesAsync(restarted);
        Assert.Contains("nas.lan", names);
        Assert.Contains("192.168.1.10", names);
    }

    /// <summary>
    /// A removed name has to go too: a certificate that still answers for an address the operator took
    /// out is a promise nobody made any more.
    /// </summary>
    [Fact]
    public async Task ARemovedLanName_IsReissued() {
        using var host = LanHost("nas.lan, 192.168.1.10");
        await EnsureAsync(host);

        using var restarted = host.Restart(LanSettings("nas.lan"));
        await EnsureAsync(restarted);

        var names = await NamesAsync(restarted);
        Assert.Equal(new[] { "nas.lan" }, names.ToArray());
    }

    /// <summary>
    /// The escape hatch an operator uses to start over: delete the CA row. Every leaf under the old root
    /// is untrusted from that moment, so the held one has to be replaced rather than served on.
    /// </summary>
    [Fact]
    public async Task AReplacedCa_IsReissuedUnder() {
        using var host = LanHost("nas.lan");
        await EnsureAsync(host);
        var before = Store(host).SelectCertificate(Host)!;
        var oldIssuer = await CaRowAsync(host);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.InternalCas.ExecuteDeleteAsync(Ct);
        }
        await EnsureAsync(host);

        var ca = await CaRowAsync(host);
        Assert.NotNull(ca);
        Assert.NotEqual(oldIssuer!.Thumbprint, ca.Thumbprint);
        // Both roots carry the same subject, so only the key identifier tells them apart — which is
        // exactly what the reissue check reads.
        Assert.NotEqual(before.Thumbprint, Store(host).SelectCertificate(Host)!.Thumbprint);
    }

    [Fact]
    public async Task NothingWantsALeaf_SoNoCaIsEvenCreated() {
        using var host = LanHost("nas.lan");

        // The production predicate, on an instance with no port routes.
        await host.Services.GetRequiredService<InternalCertificateService>().EnsureAsync(Ct);

        Assert.Null(Store(host).SelectCertificate(Host));
        // A CA that exists is a root an operator is invited to import; minting one nothing uses is noise.
        Assert.Null(await CaRowAsync(host));
    }

    [Fact]
    public async Task AnotherProvider_IssuesNothing() {
        using var host = AuthTestHost.Start(
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "caddy"),
            ("Watchtower:Proxy:Yarp:LanNames", "nas.lan"));

        await EnsureAsync(host);

        // These certificates are served by the in-process proxy's listeners; under Caddy there is
        // nothing to serve them.
        Assert.Null(Store(host).SelectCertificate(Host));
        Assert.Null(await CaRowAsync(host));
    }

    [Fact]
    public async Task NoLanNames_IssuesNothing() {
        using var host = LanHost("");

        await EnsureAsync(host);

        // A certificate that names nothing validates for nothing — and there is no sensible name to
        // invent on the operator's behalf.
        Assert.Null(Store(host).SelectCertificate(Host));
        Assert.Null(await CaRowAsync(host));
    }

    [Fact]
    public async Task UnreadableLanNames_AreReportedRatherThanThrown() {
        // Refused at the point they are typed, so this value arrived through the environment — and a
        // start must not fail over it.
        using var host = LanHost("nas.lan, not a host name");

        await EnsureAsync(host);

        Assert.Null(Store(host).SelectCertificate(Host));
    }

    /// <summary>
    /// The prune deletes expired certificates nothing routes to. The LAN leaf is never in that desired
    /// set — its host is a store key, not a domain — so without an exemption it would be deleted thirty
    /// days after expiry on a deployment that is still serving from it.
    /// </summary>
    [Fact]
    public async Task ThePrune_LeavesTheInternalLeafAlone() {
        using var host = LanHost("nas.lan");
        var now = DateTimeOffset.UtcNow;
        using var expired = TestCertificates.Create(Host, now.AddDays(-400), now.AddDays(-40));
        using var alsoExpired = TestCertificates.Create("gone.test", now.AddDays(-90), now.AddDays(-40));
        await Store(host).InstallInternalAsync(Host, expired.PemChain, expired.Key!, Ct);
        await Store(host).InstallAsync("gone.test", alsoExpired.PemChain, alsoExpired.Key!, Ct);

        var removed = await Store(host).PruneUndesiredAsync(
            new HashSet<string>(), TimeSpan.FromDays(30), Ct);

        Assert.Equal(1, removed);
        Assert.NotNull(await RowAsync(host));
        Assert.NotNull(Store(host).SelectCertificate(Host));
    }

    /// <summary>
    /// The fourth trigger, which no amount of database state can produce inside one test run: the held
    /// certificate is simply old. Asked of the decision itself, with a clock a year on.
    /// </summary>
    [Fact]
    public async Task AnAgedLeaf_IsReissued_OnceTheRenewalWindowOpens() {
        using var host = AuthTestHost.Start();
        var now = DateTimeOffset.UtcNow;
        using var root = await host.Services.GetRequiredService<InternalCaStore>().LoadOrCreateAsync(Ct);
        using var leaf = InternalCaIssuer.IssueLeaf(root.Certificate, ["nas.lan"], [], now);
        var entry = new CertificateEntry(
            Host, leaf.Certificate.NotBefore.ToUniversalTime(), leaf.Certificate.NotAfter.ToUniversalTime(),
            "Watchtower Internal CA", leaf.Certificate.Thumbprint, ChainLength: 1);

        Assert.Null(InternalCertificateService.ReissueReason(
            leaf.Certificate, entry, root.Certificate, ["nas.lan"], [], now));
        Assert.Equal(
            "renewal due",
            InternalCertificateService.ReissueReason(
                leaf.Certificate, entry, root.Certificate, ["nas.lan"], [], now.AddDays(300)));
        // And nothing held at all is the first pass, which must not be mistaken for "up to date".
        Assert.Equal(
            "none held",
            InternalCertificateService.ReissueReason(
                current: null, entry: null, root.Certificate, ["nas.lan"], [], now));
    }

    // ── What wants a leaf, and what it tells the routes (ADR-0033) ────────────

    /// <summary>
    /// The production predicate. One port-bound route is what makes a LAN certificate wanted, and one
    /// leaf covers however many there are.
    /// </summary>
    [Fact]
    public async Task APortRoute_IsWhatMakesALeafWanted() {
        using var host = LanHost("nas.lan");
        var stackId = await host.AddStackAsync("media", composeProjectName: "media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);

        await host.Services.GetRequiredService<InternalCertificateService>().EnsureAsync(Ct);

        var leaf = Store(host).SelectCertificate(Host);
        Assert.NotNull(leaf);

        // Pending with no explanation is what a freshly created route would otherwise sit at forever;
        // the certificate that makes it serveable is the thing its status is about.
        var route = await RouteAsync(host, routeId);
        Assert.Equal(RouteStatus.Active, route.Status);
        Assert.Null(route.StatusDetail);
        Assert.Equal(leaf.NotAfter.ToUniversalTime(), route.CertNotAfter?.UtcDateTime);
    }

    /// <summary>
    /// A second route created while the leaf already covers the configured names. Nothing is reissued —
    /// the same certificate serves it — but the row still has to be told that it is being served, or the
    /// operator would see a permanent Pending on a route that works.
    /// </summary>
    [Fact]
    public async Task ARouteAddedUnderAnUpToDateLeaf_IsStillMarkedActive() {
        using var host = LanHost("nas.lan");
        var stackId = await host.AddStackAsync("media", composeProjectName: "media");
        await host.AddPortRouteAsync(stackId, 9001);
        var service = host.Services.GetRequiredService<InternalCertificateService>();
        await service.EnsureAsync(Ct);
        var thumbprint = Store(host).SelectCertificate(Host)!.Thumbprint;

        var second = await host.AddPortRouteAsync(stackId, 9002, serviceName: "jellyfin");
        await service.EnsureAsync(Ct);

        Assert.Equal(thumbprint, Store(host).SelectCertificate(Host)!.Thumbprint);
        var route = await RouteAsync(host, second);
        Assert.Equal(RouteStatus.Active, route.Status);
        Assert.NotNull(route.CertNotAfter);
    }

    /// <summary>
    /// The refusal an operator is most likely to hit: a port route created before the LAN names are set.
    /// The log says so once, and the row says so where they are looking.
    /// </summary>
    [Fact]
    public async Task WithoutLanNames_ThePortRoutesSayWhyTheyAreNotServed() {
        using var host = LanHost("");
        var stackId = await host.AddStackAsync("media", composeProjectName: "media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);

        await host.Services.GetRequiredService<InternalCertificateService>().EnsureAsync(Ct);

        var route = await RouteAsync(host, routeId);
        Assert.Equal(RouteStatus.Error, route.Status);
        Assert.Contains("LAN names", route.StatusDetail ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Null(route.CertNotAfter);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>A host running the in-process proxy with LAN names configured.</summary>
    private static AuthTestHost LanHost(string lanNames) => AuthTestHost.Start(LanSettings(lanNames));

    private static (string Key, string? Value)[] LanSettings(string lanNames) => [
        ("Watchtower:Proxy:Enabled", "true"),
        ("Watchtower:Proxy:Provider", "yarp"),
        ("Watchtower:Proxy:Yarp:LanNames", lanNames),
    ];

    /// <summary>
    /// One pass with the port-route lookup stubbed. The id names no row on purpose: these tests are
    /// about issuance, and a status write that matches nothing is the honest way to say the route table
    /// is not what is under test here.
    /// </summary>
    private static Task EnsureAsync(AuthTestHost host) =>
        host.Services.GetRequiredService<InternalCertificateService>().EnsureCoreAsync(_ => Wanted, Ct);

    private static CertificateStore Store(AuthTestHost host) =>
        host.Services.GetRequiredService<CertificateStore>();

    private static async Task<ProxyCertificate?> RowAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.ProxyCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.Host == Host, Ct);
    }

    private static async Task<Route> RouteAsync(AuthTestHost host, int routeId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, Ct);
    }

    private static async Task<InternalCa?> CaRowAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.InternalCas.AsNoTracking().FirstOrDefaultAsync(Ct);
    }

    /// <summary>The names the served leaf answers for, as the API reports them.</summary>
    private static async Task<IReadOnlyList<string>> NamesAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<GetInternalCa>(scope.ServiceProvider);
        var result = await handler.HandleAsync(new GetInternalCa.Query(), Ct);
        Assert.True(result.IsSuccess);
        Assert.True(result.Value.Ca.Present);
        return result.Value.Ca.SubjectAltNames;
    }
}
