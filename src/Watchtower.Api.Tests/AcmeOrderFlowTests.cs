using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// One certificate, ordered from an ACME CA and installed — end to end, over the real protocol, through
/// the real request pipeline. ADR-0020.
/// </summary>
/// <remarks>
/// The CA here verifies every signature and fetches the challenge from Watchtower itself
/// (<see cref="FakeAcmeServer"/>), so what these tests actually pin is the interaction: a JWS the CA
/// accepts, a challenge the middleware answers on the validated host over plain HTTP, a CSR the CA can
/// sign, and a chain the certificate store can serve.
/// </remarks>
public sealed class AcmeOrderFlowTests {
    private const string Host = "app.example.invalid";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACertificateIsOrdered_Installed_AndServed() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);

        var outcome = await estate.Certificates.RenewNowAsync(Host, Ct);

        var issued = Assert.IsType<IssueOutcome.Issued>(outcome);
        Assert.Equal("Fake ACME Intermediate", issued.Issuer);

        // In the store, with the chain and a usable private key — which is what an SNI handshake needs.
        var entry = estate.Store.Find(Host);
        Assert.NotNull(entry);
        Assert.Equal(2, entry.ChainLength);
        var context = estate.Store.SelectContext(Host);
        Assert.NotNull(context);
        Assert.True(context.TargetCertificate.HasPrivateKey);
        Assert.Single(context.IntermediateCertificates);
        Assert.Equal([Host], SanNames(context.TargetCertificate));

        // The account was registered exactly once, and the challenge was actually validated.
        Assert.Equal(1, estate.Ca.AccountRegistrations);
        Assert.Equal(1, estate.Ca.ChallengesTriggered);
    }

    /// <summary>
    /// Issuance is not finished until the operator can see it. The route row is what the Routes page
    /// reads, and it carries the expiry the renewal reminder is built on.
    /// </summary>
    [Fact]
    public async Task TheRouteRowBecomesActive_WithTheCertificateExpiry() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);

        await estate.Certificates.RenewNowAsync(Host, Ct);

        var route = await estate.RouteAsync(Host);
        Assert.Equal(RouteStatus.Active, route.Status);
        Assert.Null(route.Detail);
        Assert.Equal(estate.Store.Find(Host)!.NotAfter, route.CertNotAfter);
    }

    /// <summary>
    /// The token is retracted when the order settles. Left published, it would be an answer any stranger
    /// could fetch from every host the proxy serves.
    /// </summary>
    [Fact]
    public async Task TheChallengeTokenIsRetractedAfterwards() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);

        await estate.Certificates.RenewNowAsync(Host, Ct);

        Assert.Equal(0, estate.Factory.Services.GetRequiredService<AcmeHttpChallengeStore>().Count);
    }

    [Fact]
    public async Task TheCertificateListReportsItAsActive() {
        await using var estate = await AcmeEstate.StartAsync();
        var routeId = await estate.AddRouteAsync(Host);

        await estate.Certificates.RenewNowAsync(Host, Ct);

        var listed = await estate.ListCertificatesAsync();
        var certificate = Assert.Single(listed, c => c.Host == Host);
        Assert.Equal("active", certificate.State);
        Assert.Equal("route", certificate.Source);
        Assert.Equal(routeId, certificate.RouteId);
        Assert.Equal("Fake ACME Intermediate", certificate.Issuer);
        Assert.Equal(0, certificate.ConsecutiveFailures);
        Assert.Null(certificate.LastError);
    }

    /// <summary>
    /// The reconcile's whole job is deciding <em>not</em> to order. A freshly issued certificate is a
    /// third of its life away from renewal, and a pass that ordered anyway would burn rate limit every
    /// five minutes.
    /// </summary>
    [Fact]
    public async Task AFreshCertificateIsNotReordered() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        await estate.Certificates.RenewNowAsync(Host, Ct);

        var before = estate.Ca.Requests.Count;
        await estate.Certificates.ReconcileAsync(Ct);

        Assert.Equal(before, estate.Ca.Requests.Count);
    }

    /// <summary>The other half: once inside the window, the same pass does order.</summary>
    [Fact]
    public async Task ACertificateInsideItsRenewalWindowIsReordered() {
        await using var estate = await AcmeEstate.StartAsync();
        // 90 days long and 80 days old: two thirds gone, so renewal is due.
        estate.Ca.CertificateAge = TimeSpan.FromDays(80);
        await estate.AddRouteAsync(Host);
        await estate.Certificates.RenewNowAsync(Host, Ct);
        var first = estate.Store.Find(Host)!.Thumbprint;

        await estate.Certificates.ReconcileAsync(Ct);

        Assert.NotEqual(first, estate.Store.Find(Host)!.Thumbprint);
        Assert.Equal(2, estate.Ca.ChallengesTriggered);
    }

    /// <summary>
    /// The renewal path at a CA that still holds a valid authorization: the order arrives <c>ready</c> and
    /// there is nothing to prove. Skipping the challenge is not an optimisation — re-validating would
    /// spend an attempt against the per-hostname limit for no reason.
    /// </summary>
    [Fact]
    public async Task AnOrderThatIsAlreadyReady_SkipsTheChallengeEntirely() {
        await using var estate = await AcmeEstate.StartAsync();
        estate.Ca.OrdersStartReady = true;
        await estate.AddRouteAsync(Host);

        var outcome = await estate.Certificates.RenewNowAsync(Host, Ct);

        Assert.IsType<IssueOutcome.Issued>(outcome);
        Assert.Equal(0, estate.Ca.ChallengesTriggered);
        Assert.DoesNotContain(estate.Ca.Requests, path => path.StartsWith("/authz", StringComparison.Ordinal));
    }

    /// <summary>
    /// The account is registered once and reused. It is rate-limited per key and accumulates issuance
    /// history, so a second registration would be a bug worth catching here rather than at a CA.
    /// </summary>
    [Fact]
    public async Task TheAccountIsRegisteredOnce_ForAnyNumberOfHosts() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        await estate.AddRouteAsync("other.example.invalid");

        await estate.Certificates.RenewNowAsync(Host, Ct);
        await estate.Certificates.RenewNowAsync("other.example.invalid", Ct);

        Assert.Equal(1, estate.Ca.AccountRegistrations);
        Assert.NotNull(estate.Store.Find(Host));
        Assert.NotNull(estate.Store.Find("other.example.invalid"));
    }

    /// <summary>A restart must not re-register: the account key and its URL live on the volume.</summary>
    [Fact]
    public async Task TheAccountSurvivesInTheCertificateDirectory() {
        await using var estate = await AcmeEstate.StartAsync();
        await estate.AddRouteAsync(Host);
        await estate.Certificates.RenewNowAsync(Host, Ct);

        var accounts = Path.Combine(estate.Store.RootPath, "accounts");
        var directory = Assert.Single(Directory.GetDirectories(accounts));
        Assert.True(File.Exists(Path.Combine(directory, AcmeAccountKey.KeyFileName)));
        Assert.Contains(estate.Ca.DirectoryUrl, File.ReadAllText(Path.Combine(directory, AcmeAccountKey.AccountFileName)));
    }

    private static string[] SanNames(X509Certificate2 certificate) =>
        certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>()
            .SelectMany(e => e.EnumerateDnsNames())
            .ToArray();
}
