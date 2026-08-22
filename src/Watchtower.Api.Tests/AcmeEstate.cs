using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Elarion.Abstractions;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// Stands an in-process CA and a Watchtower host up facing each other. The wiring is circular by nature —
/// Watchtower orders from the CA, the CA validates by fetching from Watchtower — so it is worth having in
/// one place rather than repeated with subtle differences.
/// </summary>
internal sealed class AcmeEstate(FakeAcmeServer ca, WatchtowerApiFactory factory) : IAsyncDisposable {
    public FakeAcmeServer Ca { get; } = ca;
    public WatchtowerApiFactory Factory { get; } = factory;

    public CertificateManager Certificates => Factory.Services.GetRequiredService<CertificateManager>();
    public CertificateStore Store => Factory.Services.GetRequiredService<CertificateStore>();
    public StubDnsPreflight Dns => Factory.Dns;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A running CA and a Watchtower host pointed at it.
    /// </summary>
    /// <param name="httpsBound">
    /// What <see cref="YarpListenerState.HttpsBound"/> says. True by default: under <c>TestServer</c>
    /// nothing binds a socket at all, so the flag has to be set by hand — and the reconcile loop refuses
    /// to issue certificates that could not be served.
    /// </param>
    /// <param name="selfCheck">
    /// Whether the issuer probes its own challenge responder before telling the CA to validate. Off by
    /// default because there is no local HTTP address under <c>TestServer</c> to probe; the one test that
    /// cares turns it on and supplies a dead one.
    /// </param>
    public static async Task<AcmeEstate> StartAsync(
        bool httpsBound = true,
        bool selfCheck = false,
        params (string Key, string? Value)[] settings) {
        var ca = new FakeAcmeServer();
        await ca.StartAsync();

        var factory = new WatchtowerApiFactory([
            ("Watchtower:Proxy:Enabled", "true"),
            ("Watchtower:Proxy:Provider", "yarp"),
            ("Watchtower:Proxy:Yarp:AcmeDirectoryUrl", ca.DirectoryUrl),
            ("Watchtower:Proxy:Yarp:AcmeSelfCheckEnabled", selfCheck ? "true" : "false"),
            ("Watchtower:Proxy:AdminEmail", "ops@example.invalid"),
            .. settings,
        ]) { AcmeTransport = ca.Transport, UseRealProxyProvider = true };

        // Touching Services builds the host, which is what makes the handler below exist.
        factory.Services.GetRequiredService<YarpListenerState>().HttpsBound = httpsBound;
        // The other half of the loop: the CA validates by fetching through Watchtower's real pipeline, so
        // the challenge middleware, the host dispatch and the HTTPS redirect are all under test.
        ca.ChallengeTransport = factory.Server.CreateHandler();

        return new AcmeEstate(ca, factory);
    }

    /// <summary>Seeds a TLS route and projects it, which is what puts the host in the desired set.</summary>
    public async Task<int> AddRouteAsync(string domain) {
        var id = await Factory.AddRouteAsync(domain, AccessMode.Public, tlsEnabled: true);
        await Factory.ApplyProxyAsync();
        return id;
    }

    /// <summary>The route row as the operator sees it.</summary>
    public async Task<RouteState> RouteAsync(string domain) {
        RouteState? found = null;
        await Factory.WithScopeAsync(async sp => {
            var route = await sp.GetRequiredService<WatchtowerDbContext>().Routes
                .AsNoTracking().FirstAsync(r => r.Domain == domain, Ct);
            found = new RouteState(route.Status, route.StatusDetail, route.CertNotAfter);
        });
        return found!;
    }

    /// <summary>
    /// <c>proxy.listCertificates</c>, through the handler the JSON-RPC endpoint dispatches to. Resolved
    /// from a scope rather than posted over HTTP because what is under test is the projection, not the
    /// transport — the RPC surface itself is covered by the schema export.
    /// </summary>
    public async Task<IReadOnlyList<CertificateDto>> ListCertificatesAsync() {
        IReadOnlyList<CertificateDto> listed = [];
        await Factory.WithScopeAsync(async sp => {
            var result = await sp
                .GetRequiredService<IHandler<ListCertificates.Query, Result<ListCertificates.Response>>>()
                .HandleAsync(new ListCertificates.Query(), Ct);
            listed = result.Value.Certificates;
        });
        return listed;
    }

    /// <summary>The manager's view of one host.</summary>
    public HostCertificateState State(string host) =>
        Certificates.Snapshot().Single(s => s.Host == host);

    public async ValueTask DisposeAsync() {
        Factory.Dispose();
        await Ca.DisposeAsync();
    }
}

/// <summary>The three columns a certificate outcome writes onto a route row.</summary>
internal sealed record RouteState(RouteStatus Status, string? Detail, DateTimeOffset? CertNotAfter);
