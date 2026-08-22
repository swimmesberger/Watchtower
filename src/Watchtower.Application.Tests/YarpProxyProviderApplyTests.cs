using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the in-process provider's projection step: the route table becomes the in-memory routing
/// table, the set of hosts the certificate manager is asked for, and the "waiting for a certificate"
/// bookkeeping. This is the whole control plane in one call — everything the request path and the
/// certificate plane later read is what <c>ApplyAsync</c> put there.
/// </summary>
/// <remarks>
/// Nothing here touches Docker. <see cref="ProxyIngressNetworks"/> is resolved from the host because
/// the provider's constructor asks for it, but <c>ApplyAsync</c> never calls it — joining containers
/// to their ingress networks belongs to the reconcile, and the projection has to keep working when the
/// daemon does not answer.
/// </remarks>
public sealed class YarpProxyProviderApplyTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string AuthHost = "login.example.invalid";

    [Fact]
    public async Task Apply_ProjectsTheRoutes_AndAsksForTheirCertificates() {
        using var host = AuthTestHost.Start();
        var stackId = await host.AddStackAsync("billing", composeProjectName: "billing");
        await host.AddRouteAsync(stackId, "app.example.invalid");
        await host.AddRouteAsync(stackId, "plain.example.invalid");
        await SetTlsAsync(host, "plain.example.invalid", false);
        await host.AddRealmAsync("acme", "acme.example.invalid");

        using var yarp = Build(host, EnabledYarp());
        await yarp.Provider.ApplyAsync(Ct);

        // Three rows: two application routes and the realm's login route. The configured Auth:Host adds
        // nothing — since ADR-0021 a served hostname is a row, and Auth:Host is only a redirect address.
        var snapshot = yarp.Table.Current;
        Assert.Equal(3, snapshot.Count);
        Assert.True(snapshot.TryGet("app.example.invalid", out var app));
        Assert.Equal(ProxyIngressNetworks.EdgeAlias("billing", "web"), app.UpstreamHost);
        Assert.Equal(8080, app.UpstreamPort);
        Assert.True(app.Tls);
        Assert.NotNull(app.RouteId);

        // The realm's login page is served by Watchtower itself, not forwarded to a stack — and it has a
        // row like any other host, which is what gives it a status and an audit trail.
        Assert.True(snapshot.TryGet("acme.example.invalid", out var login));
        Assert.True(login.Local);
        Assert.NotNull(login.RouteId);
        Assert.Equal(ProxySiteProjection.SelfAlias, login.UpstreamHost);

        // The plain-HTTP route is served but never asks for a certificate; the login host does.
        Assert.Equal(
            ["acme.example.invalid", "app.example.invalid"],
            yarp.Certs.DesiredHosts.Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain("plain.example.invalid", yarp.Certs.DesiredHosts);
        Assert.DoesNotContain(AuthHost, yarp.Certs.DesiredHosts);
    }

    [Fact]
    public async Task Apply_MarksEveryTlsHostAsWaitingForACertificate_WatchtowersOwnIncluded() {
        using var host = AuthTestHost.Start();
        var stackId = await host.AddStackAsync("billing", composeProjectName: "billing");
        await host.AddRouteAsync(stackId, "app.example.invalid");
        await host.AddRouteAsync(stackId, "plain.example.invalid");
        await SetTlsAsync(host, "plain.example.invalid", false);
        await host.AddRealmAsync("acme", "acme.example.invalid");

        using var yarp = Build(host, EnabledYarp());
        await yarp.Provider.ApplyAsync(Ct);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var byDomain = await db.Routes.AsNoTracking().ToDictionaryAsync(r => r.Domain, r => r.StatusDetail, Ct);
        Assert.Equal("Waiting for a certificate", byDomain["app.example.invalid"]);
        // Nothing is pending for a route that is served over plain HTTP.
        Assert.Null(byDomain["plain.example.invalid"]);
        // The login host reports its provisioning state like every other row — the whole point of
        // ADR-0021 is that "is my login page's certificate issued yet?" has an answer on the Routes page.
        Assert.Equal("Waiting for a certificate", byDomain["acme.example.invalid"]);
    }

    [Fact]
    public async Task Apply_WhileTheProviderIsNotSelected_ServesNothing() {
        using var host = AuthTestHost.Start();
        var stackId = await host.AddStackAsync("billing", composeProjectName: "billing");
        await host.AddRouteAsync(stackId, "app.example.invalid");

        var options = EnabledYarp();
        using var yarp = Build(host, options);
        await yarp.Provider.ApplyAsync(Ct);
        Assert.True(yarp.Table.Current.TryGet("app.example.invalid", out _));

        // Switching provider (or disabling the proxy) must leave nothing behind that could still be
        // matched — the request path reads this table without consulting the options at all.
        options.Value = With(options.Value, o => o with { Enabled = false });
        await yarp.Provider.ApplyAsync(Ct);

        Assert.Equal(0, yarp.Table.Current.Count);
        Assert.Empty(yarp.Certs.DesiredHosts);

        options.Value = With(options.Value, o => o with { Enabled = true, Provider = ProxyProviderNames.Caddy });
        await yarp.Provider.ApplyAsync(Ct);
        Assert.Equal(0, yarp.Table.Current.Count);
    }

    [Fact]
    public async Task Apply_NeverThrows_EvenWhenTheCertificatePlaneDoes() {
        using var host = AuthTestHost.Start();
        var stackId = await host.AddStackAsync("billing", composeProjectName: "billing");
        await host.AddRouteAsync(stackId, "app.example.invalid");

        var options = EnabledYarp();
        using var yarp = Build(host, options);
        yarp.Certs.SetDesiredFailure = new InvalidOperationException("the ACME account key is unreadable");

        // Best-effort by contract: route CRUD and deploys call this, and a certificate-plane fault must
        // not fail the operation that triggered it. Both the active and the inactive path.
        await yarp.Provider.ApplyAsync(Ct);
        options.Value = With(options.Value, o => o with { Enabled = false });
        await yarp.Provider.ApplyAsync(Ct);
    }

    [Fact]
    public async Task IsRunning_FollowsTheHttpsListener() {
        using var host = AuthTestHost.Start();
        var listener = host.Services.GetRequiredService<YarpListenerState>();
        using var yarp = Build(host, EnabledYarp());

        // There is no container to inspect — "running" means the listener came up.
        Assert.False(await yarp.Provider.IsRunningAsync(Ct));
        listener.HttpsBound = true;
        Assert.True(await yarp.Provider.IsRunningAsync(Ct));
    }

    [Fact]
    public async Task ForgetDomain_DropsWhatIsHeld_AndReprojects() {
        using var host = AuthTestHost.Start();
        var stackId = await host.AddStackAsync("billing", composeProjectName: "billing");
        await host.AddRouteAsync(stackId, "app.example.invalid");

        using var yarp = Build(host, EnabledYarp());
        await yarp.Provider.ForgetDomainAsync("  App.Example.Invalid  ", actor: "admin", Ct);

        Assert.Equal(["app.example.invalid"], yarp.Certs.ForgottenHosts);
    }

    [Fact]
    public async Task ForgetDomain_SurfacesAFailure() {
        using var host = AuthTestHost.Start();
        using var yarp = Build(host, EnabledYarp());
        yarp.Certs.ForgetFailure = new InvalidOperationException("the key file is read-only");

        // Unlike ApplyAsync this is not best-effort: the operator asked for a specific change and has
        // to be told when it did not happen.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => yarp.Provider.ForgetDomainAsync("app.example.invalid", actor: null, Ct));
        Assert.Contains("read-only", ex.Message);
    }

    [Fact]
    public async Task ForgetDomain_WhileTheProviderIsNotSelected_DoesNothing() {
        using var host = AuthTestHost.Start();
        var options = EnabledYarp();
        options.Value = With(options.Value, o => o with { Provider = ProxyProviderNames.Caddy });
        using var yarp = Build(host, options);

        await yarp.Provider.ForgetDomainAsync("app.example.invalid", actor: null, Ct);

        Assert.Empty(yarp.Certs.ForgottenHosts);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>The provider under test with the two collaborators the assertions read.</summary>
    private sealed class Harness(
        YarpProxyProvider provider, ProxyRouteTable table, RecordingProxyCertificateManager certs) : IDisposable {
        public YarpProxyProvider Provider { get; } = provider;
        public ProxyRouteTable Table { get; } = table;
        public RecordingProxyCertificateManager Certs { get; } = certs;

        // The provider subscribes to the options monitor and owns a CTS and a semaphore.
        public void Dispose() => Provider.Dispose();
    }

    private static Harness Build(AuthTestHost host, MutableOptionsMonitor options) {
        var certs = new RecordingProxyCertificateManager();
        var provider = ActivatorUtilities.CreateInstance<YarpProxyProvider>(host.Services, certs, options);
        return new Harness(provider, host.Services.GetRequiredService<ProxyRouteTable>(), certs);
    }

    /// <summary>Auth on (so the realm login hosts are projected) with the in-process provider selected.</summary>
    private static MutableOptionsMonitor EnabledYarp() => new(new WatchtowerOptions {
        Auth = new AuthOptions { Enabled = true, Host = AuthHost },
        Proxy = new ProxyOptions { Enabled = true, Provider = ProxyProviderNames.Yarp },
    });

    private static WatchtowerOptions With(WatchtowerOptions options, Func<ProxyOptions, ProxyOptions> change) =>
        options with { Proxy = change(options.Proxy) };

    private static async Task SetTlsAsync(AuthTestHost host, string domain, bool tls) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        await db.Routes.Where(r => r.Domain == domain)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TlsEnabled, tls), Ct);
    }

    /// <summary>An options monitor whose value the test can swap — the provider re-reads it per call.</summary>
    private sealed class MutableOptionsMonitor(WatchtowerOptions value) : IOptionsMonitor<WatchtowerOptions> {
        public WatchtowerOptions Value { get; set; } = value;
        public WatchtowerOptions CurrentValue => Value;
        public WatchtowerOptions Get(string? name) => Value;
        public IDisposable? OnChange(Action<WatchtowerOptions, string?> listener) => null;
    }
}

/// <summary>
/// Records what a provider asked the certificate plane for, and can be armed to fail. Shared with
/// <see cref="ProxyProviderRouterTests"/>, where reaching it is the proof that the router dispatched
/// to the in-process provider rather than to one of the container-managing ones.
/// </summary>
internal sealed class RecordingProxyCertificateManager : IProxyCertificateManager {
    public IReadOnlyCollection<string> DesiredHosts { get; private set; } = [];
    public List<string> ForgottenHosts { get; } = [];

    /// <summary>When set, the next desired-host update throws it. ApplyAsync must swallow it.</summary>
    public Exception? SetDesiredFailure { get; set; }

    /// <summary>When set, the next forget throws it — the failure the caller must be told about.</summary>
    public Exception? ForgetFailure { get; set; }

    public void SetDesiredHosts(IReadOnlyCollection<string> hosts) {
        if (SetDesiredFailure is not null) throw SetDesiredFailure;
        DesiredHosts = hosts;
    }

    public Task ForgetHostAsync(string host, CancellationToken ct) {
        if (ForgetFailure is not null) throw ForgetFailure;
        ForgottenHosts.Add(host);
        return Task.CompletedTask;
    }
}
