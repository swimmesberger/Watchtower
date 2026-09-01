using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
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

    /// <summary>
    /// The root is valid for ten years and a leaf for one, so nine years in the two stop composing:
    /// <c>CertificateRequest.Create</c> refuses outright to sign a certificate that outlives its issuer.
    /// Unclamped, that throw comes out of the renewal pass and puts every port route into Error with an
    /// exception about a date — nine years after anybody chose the numbers.
    /// </summary>
    [Fact]
    public void ALeafNeverOutlivesTheRootThatSignedIt() {
        using var root = ShortLivedRoot(TimeSpan.FromDays(100));

        using var leaf = InternalCaIssuer.IssueLeaf(root, ["nas.lan"], [], DateTimeOffset.UtcNow);

        // Exactly the root's expiry, not a year: the leaf ends with the anchor, which is the moment the
        // operator has to replace the CA row and re-import it on every client anyway.
        Assert.Equal(root.NotAfter.ToUniversalTime(), leaf.Certificate.NotAfter.ToUniversalTime());
    }

    /// <summary>A CA of the same shape as the stored one, with a lifetime the test chooses.</summary>
    private static X509Certificate2 ShortLivedRoot(TimeSpan lifetime) {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Watchtower Internal CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(
            new X509BasicConstraintsExtension(
                certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0,
                critical: true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        request.CertificateExtensions.Add(
            new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
        return request.CreateSelfSigned(notBefore, notBefore + lifetime);
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

    /// <summary>
    /// Two instances starting together. The insert is unconditional and the unique index decides, so
    /// what has to come out of it is one root — an operator can only have imported one, and a second
    /// would make half the leaves untrusted on every client.
    /// </summary>
    [Fact]
    public async Task TwoInstancesCreatingTheCaAtOnce_EndUpWithOne() {
        using var first = AuthTestHost.Start();
        using var second = first.Restart();

        var roots = await Task.WhenAll(
            Ca(first).LoadOrCreateAsync(Ct), Ca(second).LoadOrCreateAsync(Ct));
        using var mine = roots[0];
        using var theirs = roots[1];

        Assert.Equal(mine.Thumbprint, theirs.Thumbprint);
        await using var scope = first.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = Assert.Single(await db.InternalCas.AsNoTracking().ToListAsync(Ct));
        Assert.Equal(mine.Thumbprint, row.Thumbprint);
        // And the loser signs with the winner's key, not merely with its certificate.
        Assert.True(theirs.Certificate.HasPrivateKey);
    }

    // ── Encrypting the stored key at rest ────────────────────────────────────

    /// <summary>The passphrase the two tests below adopt; long enough for the protector to accept it.</summary>
    private const string ProtectionSecret = "a-long-enough-passphrase-for-a-test";

    /// <summary>
    /// The claim the operator documentation makes: set the key-protection secret, restart, and the
    /// stored private keys are encrypted. For the CA key that used to be true only in the sense that a
    /// <em>reissue</em> would re-encrypt it — and a converged deployment reissues its LAN leaf once every
    /// eight months, so a plaintext CA key could sit in the database for most of a year after the
    /// operator believed they had encrypted it.
    /// </summary>
    [Fact]
    public async Task AdoptingTheSecretLater_EncryptsTheStoredCaKeyAtStartup() {
        using var plain = AuthTestHost.Start();
        string thumbprint;
        using (var root = await Ca(plain).LoadOrCreateAsync(Ct)) thumbprint = root.Thumbprint;
        Assert.Equal(KeyProtector.None, (await RowAsync(plain)).Protection);

        // The restart is the whole test: the provider's constructor runs the same state initialiser the
        // host runs after migrating, and nothing here asks for a certificate.
        using var encrypting = plain.Restart(("Watchtower:Auth:KeyProtectionSecret", ProtectionSecret));

        Assert.Equal(KeyProtector.AesGcmV1, (await RowAsync(encrypting)).Protection);
        // And it is still the same CA, still openable — an encrypted row nobody can read would be worse
        // than the plain one it replaced.
        using var reloaded = await Ca(encrypting).LoadOrCreateAsync(Ct);
        Assert.Equal(thumbprint, reloaded.Thumbprint);
        Assert.True(reloaded.Certificate.HasPrivateKey);
    }

    /// <summary>
    /// And a row that is already encrypted is not rewritten on every start. Asserted through <c>xmin</c>,
    /// the transaction id PostgreSQL stamps on the physical tuple: an update relocates the tuple and
    /// changes it, so "unchanged" is evidence that no write happened rather than that the columns still
    /// look the same afterwards.
    /// </summary>
    [Fact]
    public async Task AnAlreadyEncryptedCaKey_IsLeftAloneOnTheNextStart() {
        using var first = AuthTestHost.Start(("Watchtower:Auth:KeyProtectionSecret", ProtectionSecret));
        using (var _ = await Ca(first).LoadOrCreateAsync(Ct)) { }
        var before = await RowVersionAsync(first);

        using var second = first.Restart(("Watchtower:Auth:KeyProtectionSecret", ProtectionSecret));

        Assert.Equal(before, await RowVersionAsync(second));
        Assert.Equal(KeyProtector.AesGcmV1, (await RowAsync(second)).Protection);
    }

    private static async Task<Entities.InternalCa> RowAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.InternalCas.AsNoTracking()
            .SingleAsync(c => c.Name == InternalCaNames.CaRowName, Ct);
    }

    /// <summary>The CA tuple's <c>xmin</c>, as text so no client-side xid mapping is involved.</summary>
    private static async Task<string> RowVersionAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var versions = await db.Database.SqlQueryRaw<string>(
                "SELECT xmin::text AS \"Value\" FROM internal_cas WHERE name = {0}",
                InternalCaNames.CaRowName)
            .ToListAsync(Ct);
        return Assert.Single(versions);
    }

    // ── The LAN-name parser ──────────────────────────────────────────────────

    [Fact]
    public void LanNames_ReadHostNamesAndAddressesApart() {
        Assert.True(InternalCaNames.TryParseLanNames(
            "nas.lan, 192.168.1.10\nNAS.home.arpa\r\nfd00::1,nas.lan",
            out var dnsNames, out var ips, out var reason));

        Assert.Null(reason);
        // Lowercased and deduplicated, in the order they were written.
        Assert.Equal(new[] { "nas.lan", "nas.home.arpa" }, dnsNames.ToArray());
        Assert.Equal(new[] { "192.168.1.10", "fd00::1" }, ips.Select(ip => ip.ToString()).ToArray());
    }

    /// <summary>
    /// A scope id is a fact about the machine that typed it, not about the address, and a certificate
    /// has nowhere to put one. Dropping it here is what keeps "what was configured" and "what was
    /// issued" comparable — otherwise the two never match and the leaf is reissued on every pass.
    /// </summary>
    [Fact]
    public void AnIpv6ScopeId_IsDropped() {
        Assert.True(InternalCaNames.TryParseLanNames(
            "fe80::1%3, 192.168.1.10", out _, out var ips, out var reason));

        Assert.Null(reason);
        Assert.Equal(new[] { "fe80::1", "192.168.1.10" }, ips.Select(ip => ip.ToString()).ToArray());
        Assert.All(ips, ip => Assert.DoesNotContain('%', ip.ToString()));
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

    /// <summary>
    /// The framework's parser still honours the inet_aton spellings, and every one of them means an
    /// address other than the one it looks like: <c>192.168.001.010</c> is 192.168.1.8, <c>10.0.1</c> is
    /// 10.0.0.1, <c>2130706433</c> is 127.0.0.1. Accepting them would put an address in the certificate
    /// that the operator never wrote, which is the same failure as dropping an entry — one device that
    /// cannot reach the service, discovered weeks later.
    /// </summary>
    [Theory]
    [InlineData("192.168.001.010", "192.168.1.8")]
    [InlineData("010.010.010.010", "8.8.8.8")]
    [InlineData("10.0.1", "10.0.0.1")]
    [InlineData("0x7f.1", "127.0.0.1")]
    [InlineData("2130706433", "127.0.0.1")]
    public void AnAddressInAnAlternativeSpelling_IsRefused_AndSaysWhatItWouldHaveMeant(
        string raw, string wouldMean) {
        Assert.False(InternalCaNames.TryParseLanNames(raw, out _, out _, out var reason));
        Assert.NotNull(reason);
        Assert.Contains(raw, reason, StringComparison.Ordinal);
        Assert.Contains(wouldMean, reason, StringComparison.Ordinal);
    }

    /// <summary>And the canonical spelling of the same kind of address is still accepted.</summary>
    [Fact]
    public void APlainDottedQuad_IsAccepted() {
        Assert.True(InternalCaNames.TryParseLanNames("192.168.1.10", out var names, out var ips, out _));

        Assert.Empty(names);
        Assert.Equal("192.168.1.10", Assert.Single(ips).ToString());
    }

    /// <summary>
    /// <c>nas.lan.</c> is the same name, fully qualified, and <c>DesiredHosts.TryNormalize</c> accepts it
    /// for a domain route — so refusing it here would make one spelling valid in one field of the
    /// Settings page and junk in another.
    /// </summary>
    [Fact]
    public void ATrailingDot_IsTheSameName() {
        Assert.True(InternalCaNames.TryParseLanNames("nas.lan.", out var names, out _, out var reason));

        Assert.Null(reason);
        Assert.Equal("nas.lan", Assert.Single(names));
    }

    /// <summary>Exactly one, though: a second dot leaves an empty label, which is not a name.</summary>
    [Fact]
    public void TwoTrailingDots_AreStillJunk() =>
        Assert.False(InternalCaNames.TryParseLanNames("nas.lan..", out _, out _, out _));

    private static InternalCaStore Ca(AuthTestHost host) =>
        host.Services.GetRequiredService<InternalCaStore>();
}
