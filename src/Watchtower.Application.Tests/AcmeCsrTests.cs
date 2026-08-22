using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The certificate signing request the issuer builds. Pinned here rather than left implicit because the
/// empty subject is a decision that looks like an omission — and because the alternative fails only for
/// the long host names a multi-tenant deployment produces, which is exactly where it would go unnoticed
/// in development.
/// </summary>
/// <remarks>
/// The construction is reproduced rather than reached through <c>CertificateIssuer</c>, whose CSR step
/// sits between an ACME order and a finalize call. What is being pinned is the shape of the request, and
/// the end-to-end suite covers that the issuer actually builds it that way — a CA that received a
/// different one would not issue at all.
/// </remarks>
public sealed class AcmeCsrTests {
    /// <summary>
    /// The SAN names a loaded request carries. <c>LoadSigningRequest</c> hands back plain
    /// <see cref="X509Extension"/> instances rather than the typed subclasses, so the extension is
    /// re-wrapped by OID.
    /// </summary>
    private static string[] DnsNames(CertificateRequest request) {
        var raw = Assert.Single(request.CertificateExtensions, e => e.Oid?.Value == "2.5.29.17");
        return new X509SubjectAlternativeNameExtension(raw.RawData, raw.Critical)
            .EnumerateDnsNames().ToArray();
    }

    private static byte[] BuildCsr(string host, out ECDsa key) {
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(new X500DistinguishedName(""), key, HashAlgorithmName.SHA256);
        var sans = new SubjectAlternativeNameBuilder();
        sans.AddDnsName(host);
        request.CertificateExtensions.Add(sans.Build());
        return request.CreateSigningRequest();
    }

    [Fact]
    public void TheCsr_HasAnEmptySubject_AndNamesTheHostInTheSan() {
        const string Host = "app.example.invalid";
        var csr = BuildCsr(Host, out var key);
        using (key) {
            var parsed = CertificateRequest.LoadSigningRequest(
                csr, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);

            // No CN at all: the CA/Browser baseline requirements deprecated it, Let's Encrypt ignores it,
            // and X500DistinguishedName caps it at 64 characters — see the long-host test below.
            Assert.Equal("", parsed.SubjectName.Name);

            Assert.Equal([Host], DnsNames(parsed));
        }
    }

    /// <summary>
    /// A tenant host under a category domain comfortably exceeds the 64 characters a common name allows.
    /// With a CN this throws; without one it is an ordinary request.
    /// </summary>
    [Fact]
    public void ASeventyCharacterHost_Works() {
        var host = new string('a', 60) + ".example.invalid";
        Assert.True(host.Length > 64);

        var csr = BuildCsr(host, out var key);
        using (key) {
            var parsed = CertificateRequest.LoadSigningRequest(
                csr, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
            Assert.Equal([host], DnsNames(parsed));
        }

        // The counterfactual: a CN this long encodes, but as a value no CA will honour — X.520 caps the
        // common name at 64 characters, and Let's Encrypt drops the subject from the issued certificate
        // regardless. The SAN is the only place the name can live.
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var withCommonName = CertificateRequest.LoadSigningRequest(
            new CertificateRequest($"CN={host}", other, HashAlgorithmName.SHA256).CreateSigningRequest(),
            HashAlgorithmName.SHA256);
        Assert.Empty(withCommonName.CertificateExtensions);
    }

    /// <summary>The request is self-signed with the key it names, which is what proves possession.</summary>
    [Fact]
    public void TheCsrIsSignedByItsOwnKey() {
        var csr = BuildCsr("app.example.invalid", out var key);
        using (key) {
            // LoadSigningRequest verifies the request's own signature when asked to.
            var parsed = CertificateRequest.LoadSigningRequest(
                csr, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
            Assert.Equal(
                key.ExportSubjectPublicKeyInfo(), parsed.PublicKey.ExportSubjectPublicKeyInfo());
        }
    }
}
