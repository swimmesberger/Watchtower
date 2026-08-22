using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Watchtower.Application.Tests;

/// <summary>
/// Builds throwaway certificate chains in process — a self-signed root, an intermediate, and a leaf with
/// a SAN for the host under test. Generated rather than checked in so nothing expires in a year's time
/// and so a test can ask for a validity window it needs (already expired, not valid yet) instead of
/// working around a fixture's.
/// </summary>
internal static class TestCertificates {
    /// <summary>
    /// A root → intermediate → leaf chain for <paramref name="host"/>.
    /// </summary>
    /// <param name="notBefore">Leaf validity start; defaults to an hour ago.</param>
    /// <param name="notAfter">Leaf validity end; defaults to 90 days out.</param>
    /// <param name="rsa">Issue the leaf against an RSA key pair rather than the P-256 default.</param>
    public static TestChain Create(
        string host,
        DateTimeOffset? notBefore = null,
        DateTimeOffset? notAfter = null,
        bool rsa = false) {
        var now = DateTimeOffset.UtcNow;
        // The issuers span far more than any leaf a test asks for: CertificateRequest refuses to sign
        // outside the signer's own validity period.
        var caFrom = now.AddYears(-5);
        var caTo = now.AddYears(5);

        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest(
            "CN=Watchtower Test Root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootRequest.CreateSelfSigned(caFrom, caTo);

        using var intermediateKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intermediateRequest = new CertificateRequest(
            "CN=Watchtower Test Intermediate", intermediateKey, HashAlgorithmName.SHA256);
        intermediateRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        var intermediate = intermediateRequest.Create(root, caFrom, caTo, Serial());

        var sans = new SubjectAlternativeNameBuilder();
        sans.AddDnsName(host);

        ECDsa? leafEc = null;
        RSA? leafRsa = null;
        CertificateRequest leafRequest;
        if (rsa) {
            leafRsa = RSA.Create(2048);
            leafRequest = new CertificateRequest(
                $"CN={host}", leafRsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        } else {
            leafEc = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            leafRequest = new CertificateRequest($"CN={host}", leafEc, HashAlgorithmName.SHA256);
        }
        leafRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafRequest.CertificateExtensions.Add(sans.Build());

        // The signature-generator overload rather than the certificate one: the latter insists the request
        // and the issuer use the same key algorithm, which rules out an RSA leaf under an EC issuer.
        var leaf = leafRequest.Create(
            intermediate.SubjectName,
            X509SignatureGenerator.CreateForECDsa(intermediateKey),
            notBefore ?? now.AddHours(-1),
            notAfter ?? now.AddDays(90),
            Serial());

        var pemChain = leaf.ExportCertificatePem() + "\n" + intermediate.ExportCertificatePem() + "\n";
        var keyPem = rsa ? leafRsa!.ExportPkcs8PrivateKeyPem() : leafEc!.ExportPkcs8PrivateKeyPem();
        leafRsa?.Dispose();
        intermediate.Dispose();

        return new TestChain(host, pemChain, keyPem, leaf, leafEc);
    }

    private static byte[] Serial() => RandomNumberGenerator.GetBytes(12);
}

/// <summary>
/// One generated chain. <see cref="Key"/> is null for an RSA leaf — <see cref="KeyPem"/> is what the
/// store actually consumes, and the typed key only exists for the <c>InstallAsync</c> overload.
/// </summary>
internal sealed record TestChain(string Host, string PemChain, string KeyPem, X509Certificate2 Leaf, ECDsa? Key)
    : IDisposable {
    /// <summary>Writes the chain into <paramref name="root"/> exactly as the store lays it out.</summary>
    public void WriteTo(string root, string? directoryName = null) {
        var directory = Path.Combine(root, directoryName ?? Host);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "cert.pem"), PemChain);
        File.WriteAllText(Path.Combine(directory, "key.pem"), KeyPem);
    }

    public void Dispose() {
        Leaf.Dispose();
        Key?.Dispose();
    }
}
