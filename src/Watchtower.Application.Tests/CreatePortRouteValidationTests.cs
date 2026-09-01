using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.InternalCa;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the port-route half of the route CRUD handlers (ADR-0033): what <c>proxy.createRoute</c>
/// refuses before it writes a row, what <c>proxy.updateRoute</c> will and will not change afterwards,
/// and the two handlers that have nothing to say about a route with no hostname.
/// </summary>
/// <remarks>
/// Every refusal here is a friendly restatement of something the schema or a listener would decide
/// anyway — the check constraint, the filtered unique index on <c>listen_port</c>, or Kestrel refusing
/// two endpoints on one socket. They are worth asserting because the alternative to a message is a
/// route that exists and is never served.
/// </remarks>
public sealed class CreatePortRouteValidationTests {
    private const string LanNames = "nas.lan, 192.168.1.10";

    private static readonly Action<IServiceCollection> WithRouteHandlers = services => {
        services.AddCreateRoute();
        services.AddUpdateRoute();
        services.AddDeleteRoute();
        services.AddGetAccess();
        services.AddSetAccess();
        services.RemoveAll<IProxyProvider>();
        services.AddSingleton<IProxyProvider, RecordingProxyProvider>();
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Create: the happy path ───────────────────────────────────────────────

    [Fact]
    public async Task APortRoute_IsStoredWithNoHostnameAndItsOwnListener() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");

        var result = await CreateAsync(host, PortCommand(stackId, 9001));

        Assert.True(result.IsSuccess, Describe(result));
        var dto = result.Value.Route;
        Assert.Equal("port", dto.Binding);
        Assert.Equal(9001, dto.ListenPort);
        Assert.Null(dto.Domain);
        Assert.Equal("service", dto.Target);
        Assert.True(dto.TlsEnabled);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == dto.Id, Ct);
        // The shape ck_routes_binding insists on, settled by the handler rather than taken from the
        // request: a service target, public, TLS — and no hostname to be primary or custom among.
        Assert.Equal(RouteBinding.Port, row.Binding);
        Assert.Equal(AccessMode.Public, row.AccessMode);
        Assert.False(row.IsPrimary);
        Assert.Equal(DomainKind.Managed, row.Kind);
    }

    /// <summary>The upstream is joined to the edge network and the listener set rewritten, as for a domain route.</summary>
    [Fact]
    public async Task APortRoute_ConnectsItsStackAndReloadsTheProxy() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");

        var result = await CreateAsync(host, PortCommand(stackId, 9001));
        Assert.True(result.IsSuccess, Describe(result));

        var proxy = (RecordingProxyProvider)host.Services.GetRequiredService<IProxyProvider>();
        Assert.Contains(stackId, proxy.ConnectedStacks);
        Assert.True(proxy.ApplyCount > 0);
    }

    // ── Create: the refusals ─────────────────────────────────────────────────

    [Fact]
    public async Task APortRouteToWatchtowerItself_IsRefused() {
        using var host = LanHost();
        var result = await CreateAsync(
            host, PortCommand(stackId: 0, 9001) with { Target = "watchtower", ServiceName = "", ContainerPort = 0 });

        Assert.False(result.IsSuccess);
        Assert.Contains("already served on its management port", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APortRouteWithAHostname_IsRefused() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");

        var result = await CreateAsync(host, PortCommand(stackId, 9001) with { Domain = "media.example.invalid" });

        Assert.False(result.IsSuccess);
        Assert.Contains("has no hostname", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("realm")]
    [InlineData("login")]
    public async Task APortRouteNamingARealmOrALoginHost_IsRefused(string field) {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var command = PortCommand(stackId, 9001);

        var result = await CreateAsync(
            host, field == "realm" ? command with { RealmId = Realm.SystemRealmId } : command with { MakeLoginRoute = true });

        Assert.False(result.IsSuccess);
        Assert.Contains("leave realmId and makeLoginRoute unset", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Refused rather than fixed, the way the edit handler refuses it: <c>kind</c> is optional, so a
    /// caller that filled it in said something about this route, and quietly storing something else is
    /// how create and update would end up disagreeing about one request.
    /// </summary>
    [Fact]
    public async Task APortRouteNamingADomainKind_IsRefused() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");

        var result = await CreateAsync(host, PortCommand(stackId, 9001) with { Kind = "custom" });

        Assert.False(result.IsSuccess);
        Assert.Contains("leave the kind unset", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APortRouteWithNoService_IsRefused() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");

        var result = await CreateAsync(host, PortCommand(stackId, 9001) with { ServiceName = "  " });

        Assert.False(result.IsSuccess);
        Assert.Equal("Service name is required.", result.Error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public async Task APortRouteWithAContainerPortOutOfRange_IsRefused(int containerPort) {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");

        var result = await CreateAsync(host, PortCommand(stackId, 9001) with { ContainerPort = containerPort });

        Assert.False(result.IsSuccess);
        Assert.Contains("Container port must be between 1 and 65535", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>A port route <em>is</em> its listener, so there is no such thing as one without a port.</summary>
    [Fact]
    public async Task APortRouteWithNoListenPort_IsRefused() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");

        var result = await CreateAsync(host, PortCommand(stackId, 9001) with { ListenPort = null });

        Assert.False(result.IsSuccess);
        Assert.Contains("needs a listen port", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(70000)]
    public async Task AListenPortOutOfRange_IsRefused(int listenPort) {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");

        var result = await CreateAsync(host, PortCommand(stackId, listenPort));

        Assert.False(result.IsSuccess);
        Assert.Contains("listen port must be between 1 and 65535", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Taking the management port would put a stack service on the listener Watchtower's own UI and API
    /// are served on — and take the UI down with it, from inside the page that created the route.
    /// </summary>
    [Fact]
    public async Task AListenPortOnTheManagementPort_IsRefused() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var listener = new YarpListenerState();
        listener.Publish(new YarpListenerSnapshot { ManagementPort = 8080 });

        var result = await CreateAsync(host, PortCommand(stackId, 8080), listener);

        Assert.False(result.IsSuccess);
        Assert.Contains("is the management port", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ingress listeners are shared by every domain route; a port route on one of them would capture
    /// the whole port and forward it to a single upstream.
    /// </summary>
    [Theory]
    [InlineData(18081, "HTTP ingress port")]
    [InlineData(18443, "HTTPS ingress port")]
    public async Task AListenPortOnAnIngressPort_IsRefused(int listenPort, string expected) {
        using var host = LanHost(
            ("Watchtower:Proxy:Yarp:HttpPort", "18081"), ("Watchtower:Proxy:Yarp:HttpsPort", "18443"));
        var stackId = await host.AddStackAsync("media");

        var result = await CreateAsync(host, PortCommand(stackId, listenPort));

        Assert.False(result.IsSuccess);
        Assert.Contains(expected, result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>An ingress listener that is turned off holds no port, so it cannot be in the way.</summary>
    [Fact]
    public async Task AListenPortOnAnIngressPortThatIsOff_IsAccepted() {
        using var host = LanHost(
            ("Watchtower:Proxy:Yarp:HttpPort", "0"), ("Watchtower:Proxy:Yarp:HttpsPort", "0"));
        var stackId = await host.AddStackAsync("media");

        var result = await CreateAsync(host, PortCommand(stackId, 9001));

        Assert.True(result.IsSuccess, Describe(result));
    }

    [Fact]
    public async Task AListenPortAnotherRouteHolds_IsRefused_AndNamesIt() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var takenBy = await host.AddPortRouteAsync(stackId, 9001, serviceName: "jellyfin");

        var result = await CreateAsync(host, PortCommand(stackId, 9001));

        Assert.False(result.IsSuccess);
        Assert.Contains($"already served by route {takenBy}", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains("jellyfin", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Without a LAN name the internal CA has nothing to issue for, so the route would come up on a
    /// listener no browser would trust — a failure discovered only at the first visit.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task APortRouteWithNoLanNamesConfigured_IsRefused(string lanNames) {
        using var host = AuthTestHost.Start(WithRouteHandlers, ("Watchtower:Proxy:Yarp:LanNames", lanNames));
        var stackId = await host.AddStackAsync("media");

        var result = await CreateAsync(host, PortCommand(stackId, 9001));

        Assert.False(result.IsSuccess);
        Assert.Contains("Set the LAN names in Settings", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The friendly pre-check is not the thing that decides the question: two requests, or two
    /// instances against one database, can both pass it. Staged by leaving a competing row unsaved on
    /// the handler's own context — the pre-check queries the database and misses it, and the two rows
    /// then meet in one <c>SaveChanges</c>, which is exactly the shape of the real race.
    /// </summary>
    [Fact]
    public async Task APortTakenBetweenTheCheckAndTheWrite_IsAConflict() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");

        await using var scope = host.Services.CreateAsyncScope();
        Stage(scope.ServiceProvider, stackId, 9001);
        var handler = ActivatorUtilities.CreateInstance<CreateRoute>(scope.ServiceProvider);
        var result = await handler.HandleAsync(PortCommand(stackId, 9001), Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Contains("was taken by another route", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APortRouteOnAStackThatIsNotThere_IsNotFound() {
        using var host = LanHost();
        var result = await CreateAsync(host, PortCommand(stackId: 404, 9001));

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, result.Error.Kind);
    }

    [Fact]
    public async Task AnUnknownBinding_IsRefused_RatherThanDefaulted() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");

        var result = await CreateAsync(host, PortCommand(stackId, 9001) with { Binding = "prt" });

        Assert.False(result.IsSuccess);
        Assert.Contains("Unknown route binding 'prt'", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>A hostname is what addresses a domain route; a port belongs to the other kind of row.</summary>
    [Fact]
    public async Task ADomainRouteWithAListenPort_IsRefused() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");

        var result = await CreateAsync(host, new CreateRoute.Command(
            StackId: stackId, Domain: "media.example.invalid", ServiceName: "web", ContainerPort: 8080,
            TlsEnabled: true, IsPrimary: false, Kind: null, ListenPort: 9001));

        Assert.False(result.IsSuccess);
        Assert.Contains("listenPort applies to port routes only", result.Error.Message, StringComparison.Ordinal);
    }

    // ── Update ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task APortRoute_MovesToAnotherPort() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);

        var result = await UpdateAsync(host, PortEdit(routeId) with { ListenPort = 9002 });

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(9002, result.Value.Route.ListenPort);
        // The move rewrites the derived listener setting through ApplyAsync, which is what unbinds the
        // old listener and binds the new one.
        var proxy = (RecordingProxyProvider)host.Services.GetRequiredService<IProxyProvider>();
        Assert.True(proxy.ApplyCount > 0);
    }

    /// <summary>Omitting the port is how an edit of the upstream alone leaves the address where it is.</summary>
    [Fact]
    public async Task APortRouteEditedWithoutAPort_KeepsTheOneItHas() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);

        var result = await UpdateAsync(host, PortEdit(routeId) with { ServiceName = "jellyfin", ContainerPort = 8096 });

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal(9001, result.Value.Route.ListenPort);
        Assert.Equal("jellyfin", result.Value.Route.ServiceName);
        Assert.Equal(8096, result.Value.Route.ContainerPort);
    }

    [Fact]
    public async Task APortRouteMovedOntoAnotherRoutesPort_IsRefused() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);
        var otherId = await host.AddPortRouteAsync(stackId, 9002, serviceName: "jellyfin");

        var result = await UpdateAsync(host, PortEdit(routeId) with { ListenPort = 9002 });

        Assert.False(result.IsSuccess);
        Assert.Contains($"already served by route {otherId}", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>Its own port is not a collision — a save that changes nothing else must go through.</summary>
    [Fact]
    public async Task APortRouteResavedOnItsOwnPort_IsAccepted() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);

        var result = await UpdateAsync(host, PortEdit(routeId) with { ListenPort = 9001 });

        Assert.True(result.IsSuccess, Describe(result));
    }

    [Fact]
    public async Task APortRouteMovedOntoTheManagementPort_IsRefused() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);
        var listener = new YarpListenerState();
        listener.Publish(new YarpListenerSnapshot { ManagementPort = 8080 });

        var result = await UpdateAsync(host, PortEdit(routeId) with { ListenPort = 8080 }, listener);

        Assert.False(result.IsSuccess);
        Assert.Contains("is the management port", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same rule as at creation, and the one that matters most on an edit: a move onto an ingress
    /// port would have the projection drop the listener, leaving a row that reads Active and serves
    /// nothing.
    /// </summary>
    [Theory]
    [InlineData(18081, "HTTP ingress port")]
    [InlineData(18443, "HTTPS ingress port")]
    public async Task APortRouteMovedOntoAnIngressPort_IsRefused(int listenPort, string expected) {
        using var host = LanHost(
            ("Watchtower:Proxy:Yarp:HttpPort", "18081"), ("Watchtower:Proxy:Yarp:HttpsPort", "18443"));
        var stackId = await host.AddStackAsync("media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);

        var result = await UpdateAsync(host, PortEdit(routeId) with { ListenPort = listenPort });

        Assert.False(result.IsSuccess);
        Assert.Contains(expected, result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>The edit side of the same race the create path guards; staged the same way.</summary>
    [Fact]
    public async Task APortTakenBetweenTheCheckAndTheEditsWrite_IsAConflict() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);

        await using var scope = host.Services.CreateAsyncScope();
        Stage(scope.ServiceProvider, stackId, 9002);
        var handler = ActivatorUtilities.CreateInstance<UpdateRoute>(scope.ServiceProvider);
        var result = await handler.HandleAsync(PortEdit(routeId) with { ListenPort = 9002 }, Ct);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, result.Error.Kind);
        Assert.Contains("was taken by another route", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurningAPortRouteIntoADomainRoute_IsRefused() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);

        var result = await UpdateAsync(host, PortEdit(routeId) with { Binding = "domain" });

        Assert.False(result.IsSuccess);
        Assert.Contains("binding is fixed", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TurningADomainRouteIntoAPortRoute_IsRefused() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        await host.AddRouteAsync(stackId, "media.example.invalid");
        var routeId = await DomainRouteIdAsync(host, "media.example.invalid");

        var result = await UpdateAsync(host, new UpdateRoute.Command(
            Id: routeId, Domain: "media.example.invalid", ServiceName: "web", ContainerPort: 8080,
            TlsEnabled: true, IsPrimary: false, Binding: "port", ListenPort: 9001));

        Assert.False(result.IsSuccess);
        Assert.Contains("binding is fixed", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADomainRouteEditedWithAListenPort_IsRefused() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        await host.AddRouteAsync(stackId, "media.example.invalid");
        var routeId = await DomainRouteIdAsync(host, "media.example.invalid");

        var result = await UpdateAsync(host, new UpdateRoute.Command(
            Id: routeId, Domain: "media.example.invalid", ServiceName: "web", ContainerPort: 8080,
            TlsEnabled: true, IsPrimary: false, ListenPort: 9001));

        Assert.False(result.IsSuccess);
        Assert.Contains("listenPort applies to port routes only", result.Error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("kind")]
    [InlineData("login")]
    public async Task ADomainRouteFieldOnAPortRoute_IsRefused(string field) {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);
        var edit = PortEdit(routeId);

        var result = await UpdateAsync(host, field switch {
            "domain" => edit with { Domain = "media.example.invalid" },
            "kind" => edit with { Kind = "custom" },
            _ => edit with { MakeLoginRoute = true },
        });

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorKind.Validation, result.Error.Kind);
    }

    // ── Delete ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A port route has no domain, and the delete handler used to read "no domain" as "no route" — which
    /// made every port route undeletable.
    /// </summary>
    [Fact]
    public async Task APortRoute_IsDeletable() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<DeleteRoute.Command, DeleteRoute.Response>(
            scope.ServiceProvider, new DeleteRoute.Command(routeId, RemoveFromProvider: true));

        Assert.True(result.IsSuccess, Describe(result));
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.Routes.AnyAsync(r => r.Id == routeId, Ct));
        // Nothing to forget: no provider holds a tunnel rule or a DNS record for a port.
        var proxy = (RecordingProxyProvider)host.Services.GetRequiredService<IProxyProvider>();
        Assert.Empty(proxy.Forgotten);
    }

    /// <summary>
    /// A port route has no hostname to name in the trail, so the row says <c>port {n}</c> — the only
    /// thing an operator would recognise it by. The audit row is only written when the deletion cost a
    /// realm its login host, and no handler will make a port route one, so the realm is pointed at it
    /// directly: the fallback exists precisely so that a row nothing produced cannot be recorded
    /// against a blank target.
    /// </summary>
    [Fact]
    public async Task DeletingAPortRoute_NamesItByItsPortInTheTrail() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var realm = await db.Realms.SingleAsync(r => r.Id == Realm.SystemRealmId, Ct);
        realm.LoginRouteId = routeId;
        await db.SaveChangesAsync(Ct);

        var result = await SendAsync<DeleteRoute.Command, DeleteRoute.Response>(
            scope.ServiceProvider, new DeleteRoute.Command(routeId));

        Assert.True(result.IsSuccess, Describe(result));
        var row = await db.AuditEvents.AsNoTracking().SingleAsync(e => e.Action == "route.delete", Ct);
        Assert.Equal("port 9001", row.Target);
    }

    // ── Access ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReadingAPortRoutesAccessPolicy_IsRefused() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<GetAccess.Query, GetAccess.Response>(
            scope.ServiceProvider, new GetAccess.Query(routeId));

        Assert.False(result.IsSuccess);
        Assert.Contains("always public", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GatingAPortRoute_IsRefused() {
        using var host = LanHost();
        var stackId = await host.AddStackAsync("media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<SetAccess.Command, SetAccess.Response>(
            scope.ServiceProvider,
            new SetAccess.Command(routeId, AccessMode.Authenticated, BypassPaths: null, GrantedUserIds: []));

        Assert.False(result.IsSuccess);
        Assert.Contains("always public", result.Error.Message, StringComparison.Ordinal);
    }

    // ── DNS ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The preflight the UI runs before a certificate can issue. Blank reaches it from two directions —
    /// a name not typed yet, and a port route, which has none — so the refusal has to read correctly
    /// for both rather than telling one of them about the other's situation.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CheckingDnsWithoutADomain_SaysWhatBothCallersNeedToHear(string? domain) {
        using var host = AuthTestHost.Start(services => services.AddCheckDns());

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<CheckDns.Command, CheckDns.Response>(
            scope.ServiceProvider, new CheckDns.Command(domain));

        Assert.False(result.IsSuccess);
        Assert.Equal("Enter a domain to check; a port route has none to resolve.", result.Error.Message);
    }

    // ── Certificates ─────────────────────────────────────────────────────────

    /// <summary>
    /// The LAN leaf is served but is never in the ACME desired set, so the generic refusal would tell an
    /// operator the proxy does not serve a host it plainly is serving.
    /// </summary>
    [Fact]
    public async Task RenewingTheLanCertificate_ExplainsThatItIsNotAnAcmeOne() {
        using var host = AuthTestHost.Start(services => services.AddRenewCertificate());

        await using var scope = host.Services.CreateAsyncScope();
        var result = await SendAsync<RenewCertificate.Command, RenewCertificate.Response>(
            scope.ServiceProvider, new RenewCertificate.Command(InternalCaNames.SharedLeafHost));

        Assert.False(result.IsSuccess);
        Assert.Contains("internal CA", result.Error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("is not a host", result.Error.Message, StringComparison.Ordinal);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>A host with LAN names configured — the precondition for creating a port route at all.</summary>
    private static AuthTestHost LanHost(params (string Key, string? Value)[] settings) =>
        AuthTestHost.Start(WithRouteHandlers, [("Watchtower:Proxy:Yarp:LanNames", LanNames), .. settings]);

    private static CreateRoute.Command PortCommand(int stackId, int listenPort) =>
        new(StackId: stackId, Domain: null, ServiceName: "web", ContainerPort: 8080, TlsEnabled: true,
            IsPrimary: false, Kind: null, Binding: "port", ListenPort: listenPort);

    /// <summary>An edit that changes nothing but the fields a caller sets on top of it.</summary>
    private static UpdateRoute.Command PortEdit(int routeId) =>
        new(routeId, Domain: null, ServiceName: "web", ContainerPort: 8080, TlsEnabled: true,
            IsPrimary: false, Binding: "port");

    private static async Task<Result<CreateRoute.Response>> CreateAsync(
        AuthTestHost host, CreateRoute.Command command, YarpListenerState? listener = null) {
        await using var scope = host.Services.CreateAsyncScope();
        object[] overrides = [.. new object?[] { listener }.OfType<object>()];
        var handler = ActivatorUtilities.CreateInstance<CreateRoute>(scope.ServiceProvider, overrides);
        return await handler.HandleAsync(command, Ct);
    }

    private static async Task<Result<UpdateRoute.Response>> UpdateAsync(
        AuthTestHost host, UpdateRoute.Command command, YarpListenerState? listener = null) {
        await using var scope = host.Services.CreateAsyncScope();
        object[] overrides = [.. new object?[] { listener }.OfType<object>()];
        var handler = ActivatorUtilities.CreateInstance<UpdateRoute>(scope.ServiceProvider, overrides);
        return await handler.HandleAsync(command, Ct);
    }

    /// <summary>
    /// Puts a competing port route on the handler's own context <em>without</em> saving it. The
    /// collision pre-checks query the database, so they cannot see it; both rows then reach one
    /// <c>SaveChanges</c> and meet on the unique index — the same thing that happens when two requests
    /// or two instances pass the pre-check together.
    /// </summary>
    private static void Stage(IServiceProvider scope, int stackId, int listenPort) {
        var db = scope.GetRequiredService<WatchtowerDbContext>();
        db.Routes.Add(new Route {
            Binding = RouteBinding.Port,
            StackId = stackId,
            Domain = null,
            ListenPort = listenPort,
            ServiceName = "raced",
            ContainerPort = 8080,
            TlsEnabled = true,
            AccessMode = AccessMode.Public,
            CreatedAt = DateTimeOffset.UtcNow,
        });
    }

    private static async Task<int> DomainRouteIdAsync(AuthTestHost host, string domain) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Routes.AsNoTracking().Where(r => r.Domain == domain).Select(r => r.Id).SingleAsync(Ct);
    }

    private static ValueTask<Result<TResponse>> SendAsync<TRequest, TResponse>(
        IServiceProvider scope, TRequest request) =>
        scope.GetRequiredService<IHandler<TRequest, Result<TResponse>>>().HandleAsync(request, Ct);

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
