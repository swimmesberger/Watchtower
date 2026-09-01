using System.Globalization;
using System.Net;
using System.Text;
using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
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

    private static Harness Build(AuthTestHost host, DockerEngineClient? docker = null) {
        var networks = new ProxyIngressNetworks(
            host.Services.GetRequiredService<IServiceScopeFactory>(),
            docker ?? host.Services.GetRequiredService<DockerEngineClient>(),
            NullLogger<ProxyIngressNetworks>.Instance);
        var plane = ActivatorUtilities.CreateInstance<PortRoutePlane>(host.Services, networks);
        return new Harness(plane, host.Services.GetRequiredService<ProxyRouteTable>());
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
        private readonly Handler _handler = new();

        public DockerEngineClient Client { get; }

        public RecordingDockerEngine() => Client = new DockerEngineClient("1.43", _handler, TimeSpan.FromMinutes(1));

        /// <summary>The paths of every request that changed something — the connects and the creates.</summary>
        public IReadOnlyList<string> Requests => _handler.Requests;

        /// <summary>Their bodies, where the container being joined is named.</summary>
        public IReadOnlyList<string> Bodies => _handler.Bodies;

        public void Dispose() => Client.Dispose();

        private sealed class Handler : HttpMessageHandler {
            public List<string> Requests { get; } = [];
            public List<string> Bodies { get; } = [];

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken ct) {
                var path = request.RequestUri!.AbsolutePath;
                if (request.Method == HttpMethod.Post) {
                    Requests.Add(path);
                    Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
                }

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
