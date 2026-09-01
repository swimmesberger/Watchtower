using System.Globalization;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.InternalCa;
using Watchtower.Application.Services.PortRoutes;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The port-route plane (ADR-0033 and its addendum): the port half of the route table, the listener
/// setting, the internal certificate and the network join, behind one gate — <c>Proxy:Enabled</c>.
/// </summary>
/// <remarks>
/// Every test here runs under <c>caddy</c> or <c>cloudflare</c> on purpose. That is the whole claim of
/// the addendum: a port route's listener is on Watchtower's own container, so which provider terminates
/// the public domains has nothing to say about it — and under those two providers
/// <see cref="YarpProxyProvider"/> never runs at all, so anything the plane does not do itself is not
/// done.
/// </remarks>
[Collection(HostnameEnvironment.Name)]
public sealed class PortRoutePlaneTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The host half is <see cref="YarpProxyProvider"/>'s and is empty here because that provider is not
    /// the selected one — which is exactly the deployment the old provider gate left with no listeners,
    /// no setting and no certificate.
    /// </summary>
    [Theory]
    [InlineData(ProxyProviderNames.Caddy)]
    [InlineData(ProxyProviderNames.Cloudflare)]
    public async Task Apply_ProjectsThePortHalf_WhileTheHostHalfIsEmpty(string provider) {
        using var host = Host(provider);
        var stackId = await host.AddStackAsync("media", composeProjectName: "media");
        var routeId = await host.AddPortRouteAsync(
            stackId, 9002, serviceName: "jellyfin", containerPort: 8096);
        await host.AddPortRouteAsync(stackId, 9001);
        // A domain route on the same stack, to pin that the plane projects only its own half.
        await host.AddRouteAsync(stackId, "app.example.invalid");

        using var plane = Build(host);
        await plane.Plane.ApplyAsync(Ct);

        Assert.True(plane.Table.Current.TryGetByPort(9002, out var row));
        Assert.Equal(ProxyIngressNetworks.EdgeAlias("media", "jellyfin"), row.UpstreamHost);
        Assert.Equal(8096, row.UpstreamPort);
        Assert.Equal(routeId, row.RouteId);
        Assert.Equal([9001, 9002], plane.Table.Current.PortRoutePorts.Order().ToArray());

        // Nothing of the host half, and nothing in the ACME desired set: the domain route is the yarp
        // provider's to publish, and under Caddy it publishes nothing.
        Assert.Equal(0, plane.Table.Current.Count);
        Assert.Empty(plane.Table.Current.TlsHosts);
    }

    /// <summary>
    /// The listener setting the Kestrel projection reads before the host exists, under its post-addendum
    /// name. The old name is not written: a build that still read it would be reading a value nothing
    /// keeps in step with the rows.
    /// </summary>
    [Fact]
    public async Task Apply_WritesTheRenamedListenerSetting() {
        using var host = Host(ProxyProviderNames.Caddy);
        var stackId = await host.AddStackAsync("media", composeProjectName: "media");
        var routeId = await host.AddPortRouteAsync(stackId, 9002);
        await host.AddPortRouteAsync(stackId, 9001);

        using var plane = Build(host);
        await plane.Plane.ApplyAsync(Ct);

        Assert.Equal("9001,9002", await SettingAsync(host, WatchtowerSettingPaths.ProxyPortRoutesPorts));
        Assert.Null(await SettingAsync(host, PortRouteSettingsMigration.LegacyPorts));

        // …and the same funnel takes a listener away again, which is what makes "the rows say so" and "a
        // socket is bound" one statement.
        await DeleteRouteAsync(host, routeId);
        await plane.Plane.ApplyAsync(Ct);

        Assert.Equal("9001", await SettingAsync(host, WatchtowerSettingPaths.ProxyPortRoutesPorts));
        Assert.False(plane.Table.Current.TryGetByPort(9002, out _));
    }

    /// <summary>
    /// Every instance runs this on every pass — on startup, on each route change, on each cross-instance
    /// signal. A converged one writes nothing, or the settings store would take a write per instance per
    /// pass for a value nobody moved.
    /// </summary>
    [Fact]
    public async Task Apply_OnAConvergedInstance_WritesNothing() {
        using var host = Host(ProxyProviderNames.Caddy);
        var stackId = await host.AddStackAsync("media", composeProjectName: "media");
        await host.AddPortRouteAsync(stackId, 9001);

        using var plane = Build(host);
        await plane.Plane.ApplyAsync(Ct);
        var first = await SettingVersionAsync(host);

        await plane.Plane.ApplyAsync(Ct);
        await plane.Plane.ApplyAsync(Ct);

        Assert.Equal(first, await SettingVersionAsync(host));
    }

    /// <summary>
    /// The certificate a port-route listener presents, issued under a provider that has no listener of
    /// its own to lend — the ADR-0033 addendum's second half. Cheap and idempotent, which is what lets
    /// the plane call it at the tail of every pass.
    /// </summary>
    [Fact]
    public async Task Apply_IssuesTheLanCertificate_UnderAnotherProvider() {
        using var host = Host(ProxyProviderNames.Cloudflare);
        var stackId = await host.AddStackAsync("media", composeProjectName: "media");
        var routeId = await host.AddPortRouteAsync(stackId, 9001);

        using var plane = Build(host);
        await plane.Plane.ApplyAsync(Ct);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.NotNull(await db.InternalCas.AsNoTracking().FirstOrDefaultAsync(Ct));
        // …and the outcome is on the route, so the Routes page stops saying "unsupported".
        var route = await db.Routes.AsNoTracking().SingleAsync(r => r.Id == routeId, Ct);
        Assert.Equal(Entities.RouteStatus.Active, route.Status);
        Assert.Null(route.StatusDetail);
    }

    /// <summary>
    /// The upstream hop. Watchtower's own container joins the ingress network of a stack it port-routes,
    /// under a provider whose own container joins the same network for the domain routes — the exposure
    /// the addendum's consequences section names, reaching exactly the port-routed stacks.
    /// </summary>
    [Fact]
    public async Task ConnectStack_JoinsWatchtowerToThePortRoutedStacksNetwork() {
        using var host = Host(ProxyProviderNames.Caddy);
        var stackId = await host.AddStackAsync("media", composeProjectName: "media");
        await host.AddPortRouteAsync(stackId, 9001, serviceName: "jellyfin");

        using var docker = new RecordingDockerEngine();
        using var plane = Build(host, docker.Client);
        using (HostnameEnvironment.Set("watchtower-self")) await plane.Plane.ConnectStackAsync(stackId, Ct);

        var network = $"{ProxyIngressNetworks.IngressNetworkPrefix}{stackId}";
        Assert.Contains(
            docker.Requests, path => path.EndsWith($"/networks/{network}/connect", StringComparison.Ordinal));
        Assert.Contains(docker.Bodies, body => body.Contains("watchtower-self", StringComparison.Ordinal));
    }

    /// <summary>
    /// A stack whose routes are all domains is not the plane's business: joining Watchtower to its
    /// ingress network would be exposure bought for nothing, since Watchtower has no listener for it.
    /// </summary>
    [Fact]
    public async Task ConnectStack_IgnoresAStackWithNoPortRoutes() {
        using var host = Host(ProxyProviderNames.Caddy);
        var stackId = await host.AddStackAsync("billing", composeProjectName: "billing");
        await host.AddRouteAsync(stackId, "app.example.invalid");

        using var docker = new RecordingDockerEngine();
        using var plane = Build(host, docker.Client);
        using (HostnameEnvironment.Set("watchtower-self")) await plane.Plane.ConnectStackAsync(stackId, Ct);

        Assert.Empty(docker.Requests);
    }

    /// <summary>
    /// Under yarp the domain provider and this plane each run their own startup reconcile behind their
    /// own lock, and both reach <c>EnsureNetworkAsync</c>'s check-then-create for the same per-stack
    /// ingress network. Losing that race used to throw — the daemon answers 409 — and cost that stack its
    /// upstream hop for the whole pass. It is read as already-created, the same rule the connect applies
    /// to the 403 for an endpoint that is already attached.
    /// </summary>
    [Fact]
    public async Task ConnectStack_TreatsAConflictingNetworkCreateAsAlreadyCreated() {
        using var host = Host(ProxyProviderNames.Yarp);
        var stackId = await host.AddStackAsync("media", composeProjectName: "media");
        await host.AddPortRouteAsync(stackId, 9001, serviceName: "jellyfin");

        using var docker = new RecordingDockerEngine(conflictOnCreate: true);
        using var plane = Build(host, docker.Client);
        using (HostnameEnvironment.Set("watchtower-self")) await plane.Plane.ConnectStackAsync(stackId, Ct);

        // The create lost the race and the connect still happened, which is the whole point: the loser
        // must not skip the stack it was about to join.
        var network = $"{ProxyIngressNetworks.IngressNetworkPrefix}{stackId}";
        Assert.Contains(
            docker.Requests, path => path.EndsWith($"/networks/{network}/connect", StringComparison.Ordinal));
    }

    /// <summary>
    /// The one gate there is. With the proxy off the port half is emptied and nothing else happens — in
    /// particular the listener setting is not written, so a disabled instance cannot rebind Kestrel on
    /// behalf of a deployment that has switched the proxy off.
    /// </summary>
    [Fact]
    public async Task Apply_WithTheProxyDisabled_ServesNothingAndWritesNothing() {
        using var host = AuthTestHost.Start(
            ("Watchtower:Proxy:Enabled", "false"),
            ("Watchtower:Proxy:Provider", ProxyProviderNames.Caddy),
            ("Watchtower:Proxy:PortRoutes:LanNames", "nas.lan"));
        var stackId = await host.AddStackAsync("media", composeProjectName: "media");
        await host.AddPortRouteAsync(stackId, 9001);

        using var plane = Build(host);
        await plane.Plane.ApplyAsync(Ct);

        Assert.Empty(plane.Table.Current.PortRoutePorts);
        Assert.Null(await SettingAsync(host, WatchtowerSettingPaths.ProxyPortRoutesPorts));
    }

    // ── The LAN names are a settings change nothing else carries ─────────────

    /// <summary>
    /// <c>proxy.updateConfig</c> writes the row and returns — a settings save is not a route change, so
    /// nothing calls <c>ApplyAsync</c>. Before the ADR-0033 addendum a LAN-names edit reached the
    /// certificate through <see cref="YarpProxyProvider"/>'s options subscription, whose Refresh
    /// transition ended in the <c>EnsureAsync</c> tail that moved into this plane. Watching only the
    /// enablement flag would leave an operator who adds an address waiting for the five-minute
    /// certificate reconcile under yarp — and waiting forever under the providers that do not run one.
    /// </summary>
    [Fact]
    public async Task ChangingTheLanNames_ReissuesTheCertificate() {
        using var host = Host(ProxyProviderNames.Caddy);
        var stackId = await host.AddStackAsync("media", composeProjectName: "media");
        await host.AddPortRouteAsync(stackId, 9001);

        var options = new MutableProxyOptions(host, "nas.lan");
        using var plane = Build(host, options: options);
        await plane.Plane.ApplyAsync(Ct);
        var first = LeafAsync(host);
        Assert.NotNull(first);
        Assert.Equal(["nas.lan"], SubjectAltNames(first));

        options.SetLanNames("nas.lan, nas.local");
        await WaitForAsync(() => SubjectAltNames(LeafAsync(host)).Length == 2);

        Assert.Equal(["nas.lan", "nas.local"], SubjectAltNames(LeafAsync(host)));
    }

    /// <summary>
    /// The other half of the same rule. Every unrelated proxy setting raises the options monitor, and a
    /// no-op save re-writes the identical string; neither may cost an issuance, because reissuing mints
    /// a new leaf and every device that pinned the old one would notice.
    /// </summary>
    [Fact]
    public async Task AnIdenticalLanNamesValue_ReissuesNothing() {
        using var host = Host(ProxyProviderNames.Caddy);
        var stackId = await host.AddStackAsync("media", composeProjectName: "media");
        await host.AddPortRouteAsync(stackId, 9001);

        var options = new MutableProxyOptions(host, "nas.lan");
        using var plane = Build(host, options: options);
        await plane.Plane.ApplyAsync(Ct);
        var before = LeafAsync(host)!.Thumbprint;

        // The same string, and then a change to a setting this plane does not act on.
        options.SetLanNames("nas.lan");
        options.Raise();
        await Task.Delay(200, Ct);

        Assert.Equal(before, LeafAsync(host)!.Thumbprint);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>A host with the proxy on, LAN names configured, and a provider that is not yarp.</summary>
    private static AuthTestHost Host(string provider) => AuthTestHost.Start(
        ("Watchtower:Proxy:Enabled", "true"),
        ("Watchtower:Proxy:Provider", provider),
        ("Watchtower:Proxy:PortRoutes:LanNames", "nas.lan, 192.168.1.10"));

    private sealed class Harness(PortRoutePlane plane, ProxyRouteTable table) : IDisposable {
        public PortRoutePlane Plane { get; } = plane;
        public ProxyRouteTable Table { get; } = table;

        // The plane subscribes to the options monitor and owns a CTS and a semaphore.
        public void Dispose() => Plane.Dispose();
    }

    private static Harness Build(
        AuthTestHost host, DockerEngineClient? docker = null, MutableProxyOptions? options = null) {
        var networks = new ProxyIngressNetworks(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            docker ?? host.Services.GetRequiredService<DockerEngineClient>(),
            NullLogger<ProxyIngressNetworks>.Instance);
        // The certificate service reads the LAN names, so it has to be built on the same monitor the
        // plane is — resolved from the container it would answer to the host's fixed configuration and
        // the reissue under test could not happen.
        var plane = options is null
            ? ActivatorUtilities.CreateInstance<PortRoutePlane>(host.Services, networks)
            : ActivatorUtilities.CreateInstance<PortRoutePlane>(
                host.Services,
                networks,
                ActivatorUtilities.CreateInstance<InternalCertificateService>(
                    host.Services, (IOptionsMonitor<WatchtowerOptions>)options),
                (IOptionsMonitor<WatchtowerOptions>)options);
        return new Harness(plane, host.Services.GetRequiredService<ProxyRouteTable>());
    }

    /// <summary>The leaf the port routes are served with, or null when none is held.</summary>
    private static X509Certificate2? LeafAsync(AuthTestHost host) =>
        host.Services.GetRequiredService<CertificateStore>()
            .SelectCertificate(InternalCaNames.SharedLeafHost);

    /// <summary>The DNS names a leaf answers for, sorted — what a reissue is observed by.</summary>
    private static string[] SubjectAltNames(X509Certificate2? leaf) {
        if (leaf is null) return [];
        var extension = leaf.Extensions[SubjectAltNameOid] switch {
            X509SubjectAlternativeNameExtension typed => typed,
            { } raw => new X509SubjectAlternativeNameExtension(raw.RawData, raw.Critical),
            _ => null,
        };
        return extension is null ? [] : extension.EnumerateDnsNames().Order(StringComparer.Ordinal).ToArray();
    }

    private const string SubjectAltNameOid = "2.5.29.17";

    /// <summary>
    /// Polls until <paramref name="condition"/> holds. The plane answers an options change on a
    /// background pass — that is the production shape, and the alternative would be asserting on a task
    /// the plane deliberately does not hand out.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition) {
        for (var i = 0; i < 100; i++) {
            if (condition()) return;
            await Task.Delay(50, Ct);
        }
        Assert.Fail("The plane did not react to the settings change within five seconds.");
    }

    /// <summary>
    /// The host's real options with the LAN names swappable, so a settings save can be simulated the way
    /// the configuration reload delivers one: a new value, then the monitor's callbacks.
    /// </summary>
    private sealed class MutableProxyOptions : IOptionsMonitor<WatchtowerOptions> {
        private readonly List<Action<WatchtowerOptions, string?>> _listeners = [];

        public MutableProxyOptions(AuthTestHost host, string lanNames) {
            var current = host.Services.GetRequiredService<IOptionsMonitor<WatchtowerOptions>>().CurrentValue;
            CurrentValue = current with {
                Proxy = current.Proxy with { PortRoutes = new PortRouteOptions { LanNames = lanNames } },
            };
        }

        public WatchtowerOptions CurrentValue { get; private set; }

        public WatchtowerOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<WatchtowerOptions, string?> listener) {
            _listeners.Add(listener);
            return null;
        }

        public void SetLanNames(string lanNames) {
            CurrentValue = CurrentValue with {
                Proxy = CurrentValue.Proxy with { PortRoutes = new PortRouteOptions { LanNames = lanNames } },
            };
            Raise();
        }

        /// <summary>An options change that moved nothing this plane reads.</summary>
        public void Raise() {
            foreach (var listener in _listeners) listener(CurrentValue, null);
        }
    }

    private static async Task<string?> SettingAsync(AuthTestHost host, string path) {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISettingsManager>()
            .GetStringAsync(path, SettingsScope.Global, Ct);
    }

    /// <summary>
    /// The stored row's version, straight out of the settings store's own table: it moves on a write and
    /// only on a write, which is the thing "wrote nothing" has to be measured by. Read with SQL because
    /// the settings API deliberately does not expose it.
    /// </summary>
    private static async Task<long> SettingVersionAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """SELECT version FROM elarion_settings WHERE kind = 'global' AND "key" = @key""";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = WatchtowerSettingPaths.ProxyPortRoutesPorts;
        command.Parameters.Add(parameter);
        var version = await command.ExecuteScalarAsync(Ct);
        Assert.NotNull(version);
        return Convert.ToInt64(version, CultureInfo.InvariantCulture);
    }

    private static async Task DeleteRouteAsync(AuthTestHost host, int routeId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Routes.Where(r => r.Id == routeId).ExecuteDeleteAsync(Ct);
    }

    /// <summary>
    /// A Docker daemon that says yes to everything and remembers what it was asked. Enough to observe
    /// which network Watchtower was joined to, which is the only Docker fact these tests are about.
    /// </summary>
    private sealed class RecordingDockerEngine : IDisposable {
        private readonly Handler _handler;

        public DockerEngineClient Client { get; }

        /// <param name="conflictOnCreate">
        /// Answer <c>POST /networks/create</c> with 409, the way the daemon does when the network is
        /// already there — the race between two reconciles that both passed the ListNetworks check.
        /// </param>
        public RecordingDockerEngine(bool conflictOnCreate = false) {
            _handler = new Handler { ConflictOnCreate = conflictOnCreate };
            Client = new DockerEngineClient("1.43", _handler, TimeSpan.FromMinutes(1));
        }

        /// <summary>The paths of every request that changed something — the connects and the creates.</summary>
        public IReadOnlyList<string> Requests => _handler.Requests;

        /// <summary>Their bodies, where the container being joined is named.</summary>
        public IReadOnlyList<string> Bodies => _handler.Bodies;

        public void Dispose() => Client.Dispose();

        private sealed class Handler : HttpMessageHandler {
            public List<string> Requests { get; } = [];
            public List<string> Bodies { get; } = [];
            public bool ConflictOnCreate { get; init; }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct) {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Post) {
                    Requests.Add(path);
                    Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
                }

                if (ConflictOnCreate && path.EndsWith("/networks/create", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.Conflict) {
                        Content = new StringContent(
                            """{"message":"network with name watchtower-ingress-1 already exists"}""",
                            Encoding.UTF8, "application/json"),
                    };

                var json = path.EndsWith("/networks", StringComparison.Ordinal) ? "[]"
                    : path.EndsWith("/containers/json", StringComparison.Ordinal)
                        ? """[{"Id":"media-jellyfin-1","Names":["/media-jellyfin-1"]}]"""
                    : path.EndsWith("/networks/create", StringComparison.Ordinal) ? """{"Id":"net-1"}"""
                    : "{}";
                return new HttpResponseMessage(HttpStatusCode.OK) {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }
        }
    }
}
