using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.InternalCa;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// What Watchtower's own CA puts in a certificate. The shapes here are not cosmetic: a leaf without the
/// server-authentication EKU is refused by Kestrel before any client sees it, an address in the wrong
/// kind of subject alternative name is invisible to the browser that asked for it, and an AIA extension
/// would point a LAN client at a URL it has no route to.
/// </summary>
public sealed class InternalCaIssuerTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const string AuthorityInformationAccessOid = "1.3.6.1.5.5.7.1.1";
    private const string CrlDistributionPointsOid = "2.5.29.31";

    /// <summary>
    /// The leaf is stored under this name, and the store validates every name it is handed — so a
    /// constant that could not be stored would fail at the first issuance rather than here.
    /// </summary>
    [Fact]
    public void TheSharedLeafHost_IsAStorableName() =>
        Assert.Equal(
            InternalCaNames.SharedLeafHost, CertificateStore.NormalizeHost(InternalCaNames.SharedLeafHost));

    [Fact]
    public async Task TheRoot_MaySignLeavesAndNothingElse() {
        using var host = AuthTestHost.Start();
        using var root = await Ca(host).LoadOrCreateAsync(Ct);
        var certificate = root.Certificate;

        var constraints = Assert.Single(
            certificate.Extensions.OfType<X509BasicConstraintsExtension>());
        Assert.True(constraints.CertificateAuthority);
        Assert.True(constraints.Critical);
        // Path length 0: this root can sign leaves, but a leaked key could not mint a second CA under it.
        Assert.True(constraints.HasPathLengthConstraint);
        Assert.Equal(0, constraints.PathLengthConstraint);

        var usage = Assert.Single(certificate.Extensions.OfType<X509KeyUsageExtension>());
        Assert.Equal(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, usage.KeyUsages);

        Assert.Equal("CN=Watchtower Internal CA", certificate.Subject);
        // Self-signed, and long-lived because replacing it costs a manual import on every client.
        Assert.Equal(certificate.Subject, certificate.Issuer);
        Assert.True(certificate.NotAfter - certificate.NotBefore > TimeSpan.FromDays(365 * 9));
        Assert.NotNull(certificate.GetECDsaPrivateKey());
    }

    [Fact]
    public async Task TheLeaf_NamesEveryConfiguredAddress_InTheRightKindOfSan() {
        using var host = AuthTestHost.Start();
        using var root = await Ca(host).LoadOrCreateAsync(Ct);

        using var leaf = InternalCaIssuer.IssueLeaf(
            root.Certificate, ["nas.lan", "nas.home.arpa"],
            [IPAddress.Parse("192.168.1.10"), IPAddress.Parse("fd00::1")], DateTimeOffset.UtcNow);

        var san = Assert.Single(leaf.Certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>());
        Assert.Equal(new[] { "nas.lan", "nas.home.arpa" }, san.EnumerateDnsNames().ToArray());
        // A browser asked for a bare address looks only here, and never at the DNS entries.
        Assert.Equal(
            new[] { "192.168.1.10", "fd00::1" },
            san.EnumerateIPAddresses().Select(ip => ip.ToString()).ToArray());
    }

    /// <summary>
    /// Kestrel's <c>EnsureCertificateIsAllowedForServerAuth</c> rejects a certificate whose extended key
    /// usage does not include server authentication, so this one extension is the difference between a
    /// working listener and one that refuses every handshake.
    /// </summary>
    [Fact]
    public async Task TheLeaf_IsAllowedForServerAuthentication() {
        using var host = AuthTestHost.Start();
        using var root = await Ca(host).LoadOrCreateAsync(Ct);

        using var leaf = InternalCaIssuer.IssueLeaf(
            root.Certificate, ["nas.lan"], [], DateTimeOffset.UtcNow);

        var eku = Assert.Single(leaf.Certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Contains(eku.EnhancedKeyUsages.Cast<Oid>(), o => o.Value == ServerAuthenticationOid);

        var constraints = Assert.Single(leaf.Certificate.Extensions.OfType<X509BasicConstraintsExtension>());
        Assert.False(constraints.CertificateAuthority);
        var usage = Assert.Single(leaf.Certificate.Extensions.OfType<X509KeyUsageExtension>());
        Assert.Equal(X509KeyUsageFlags.DigitalSignature, usage.KeyUsages);
    }

    /// <summary>
    /// Both would send a client off to a URL for the issuer certificate or a revocation list — over a
    /// network that, by construction, may have no route anywhere. Chain building has to be purely local.
    /// </summary>
    [Fact]
    public async Task TheLeaf_PointsAtNothingItCannotReach() {
        using var host = AuthTestHost.Start();
        using var root = await Ca(host).LoadOrCreateAsync(Ct);

        using var leaf = InternalCaIssuer.IssueLeaf(
            root.Certificate, ["nas.lan"], [], DateTimeOffset.UtcNow);

        Assert.DoesNotContain(
            leaf.Certificate.Extensions.Cast<X509Extension>(),
            e => e.Oid?.Value is AuthorityInformationAccessOid or CrlDistributionPointsOid);
    }

    [Fact]
    public async Task TheLeaf_ChainsToTheRoot_WithNothingElseTrusted() {
        using var host = AuthTestHost.Start();
        using var root = await Ca(host).LoadOrCreateAsync(Ct);
        using var leaf = InternalCaIssuer.IssueLeaf(
            root.Certificate, ["nas.lan"], [IPAddress.Parse("192.168.1.10")], DateTimeOffset.UtcNow);

        using var chain = new X509Chain();
        // Exactly the client's situation after importing the root: this one anchor, and no machine store.
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root.Certificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        Assert.True(
            chain.Build(leaf.Certificate),
            string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim())));
        Assert.Equal(2, chain.ChainElements.Count);
    }

    [Fact]
    public async Task TheLeaf_IsValidFromSlightlyBeforeNow_ForAYear() {
        using var host = AuthTestHost.Start();
        using var root = await Ca(host).LoadOrCreateAsync(Ct);
        var now = DateTimeOffset.UtcNow;

        using var leaf = InternalCaIssuer.IssueLeaf(root.Certificate, ["nas.lan"], [], now);

        var notBefore = leaf.Certificate.NotBefore.ToUniversalTime();
        var notAfter = leaf.Certificate.NotAfter.ToUniversalTime();
        // Backdated a little, because clocks on a LAN disagree — but well inside the store's own
        // not-yet-valid tolerance, or it would be stored and not served.
        Assert.InRange(now - notBefore, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(6));
        Assert.Equal(TimeSpan.FromDays(365), notAfter - notBefore);
        // Renewal at two thirds of the lifetime, per the shared policy — not on the day it expires.
        Assert.False(CertificateRenewalPolicy.IsRenewalDue(now, notBefore, notAfter));
        Assert.True(CertificateRenewalPolicy.IsRenewalDue(now.AddDays(300), notBefore, notAfter));
    }

    [Fact]
    public async Task TwoLeaves_HaveDifferentSerialNumbers() {
        using var host = AuthTestHost.Start();
        using var root = await Ca(host).LoadOrCreateAsync(Ct);

        using var first = InternalCaIssuer.IssueLeaf(root.Certificate, ["nas.lan"], [], DateTimeOffset.UtcNow);
        using var second = InternalCaIssuer.IssueLeaf(root.Certificate, ["nas.lan"], [], DateTimeOffset.UtcNow);

        Assert.NotEqual(first.Certificate.SerialNumber, second.Certificate.SerialNumber);
        Assert.NotEqual(first.Certificate.Thumbprint, second.Certificate.Thumbprint);
    }

    [Fact]
    public async Task ALeafWithNoNames_IsRefused() {
        using var host = AuthTestHost.Start();
        using var root = await Ca(host).LoadOrCreateAsync(Ct);

        // A certificate that names nothing validates for nothing; issuing one would only look like it
        // worked.
        Assert.Throws<ArgumentException>(
            () => InternalCaIssuer.IssueLeaf(root.Certificate, [], [], DateTimeOffset.UtcNow));
    }

    // ── The LAN-name parser ──────────────────────────────────────────────────

    [Fact]
    public void LanNames_ReadHostNamesAndAddressesApart() {
        Assert.True(InternalCaNames.TryParseLanNames(
            "nas.lan, 192.168.1.10\nNAS.home.arpa\r\nfd00::1; nas.lan",
            out var dnsNames, out var ips, out var reason));

        Assert.Null(reason);
        // Lowercased and deduplicated, in the order they were written.
        Assert.Equal(new[] { "nas.lan", "nas.home.arpa" }, dnsNames.ToArray());
        Assert.Equal(new[] { "192.168.1.10", "fd00::1" }, ips.Select(ip => ip.ToString()).ToArray());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" , \n ")]
    public void NoLanNames_IsNotAFailure(string? raw) {
        // Empty means the internal CA is unused, which is the state of every deployment that never
        // wanted one — refusing it would make the field impossible to clear.
        Assert.True(InternalCaNames.TryParseLanNames(raw, out var dnsNames, out var ips, out var reason));
        Assert.Empty(dnsNames);
        Assert.Empty(ips);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("nas.lan, http://nas.lan", "http://nas.lan")]
    [InlineData("nas .lan", "nas .lan")]
    [InlineData("*.lan", "*.lan")]
    [InlineData("nas.lan:9001", "nas.lan:9001")]
    [InlineData("192.168.1.10/24", "192.168.1.10/24")]
    public void AJunkEntry_FailsTheWholeParse_AndSaysWhich(string raw, string offender) {
        // Dropping it instead would issue for four names out of five and surface weeks later as one
        // device that cannot reach the service.
        Assert.False(InternalCaNames.TryParseLanNames(raw, out _, out _, out var reason));
        Assert.NotNull(reason);
        Assert.Contains(offender, reason);
    }

    private static InternalCaStore Ca(AuthTestHost host) =>
        host.Services.GetRequiredService<InternalCaStore>();
}
