using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Watchtower.Application.Tests;

/// <summary>
/// Sweeps the test intermediates a run leaves in the user's certificate store. On Windows,
/// <c>SslStreamCertificateContext.Create</c> persists the extra certificates it is handed into the
/// CurrentUser intermediate CA store so SChannel can serve the chain — so every
/// <see cref="TestCertificates"/> chain the certificate store materializes strands one
/// "CN=Watchtower Test Intermediate" there. Left alone they accumulate across runs until CryptoAPI's
/// chain builder gives up on the pile of same-subject issuers ("An unknown chain building error
/// occurred") and every certificate test on the machine fails.
/// </summary>
public sealed class LeakedCertificateCleanupFixture : IDisposable {
    /// <summary>The intermediate subject <see cref="TestCertificates"/> issues under.</summary>
    private const string TestIntermediateSubject = "CN=Watchtower Test Intermediate";

    public void Dispose() {
        if (!OperatingSystem.IsWindows()) return; // Other platforms keep the chain in memory.

        using var store = new X509Store(StoreName.CertificateAuthority, StoreLocation.CurrentUser);
        try {
            store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
        } catch (CryptographicException) {
            return; // A store this run cannot clean must not fail the run.
        }

        var leaked = store.Certificates.Find(
            X509FindType.FindBySubjectDistinguishedName, TestIntermediateSubject, validOnly: false);
        foreach (var certificate in leaked) {
            try {
                store.Remove(certificate);
            } catch (CryptographicException) {
                // Another test process may be racing the same sweep; the next run gets it.
            }
            certificate.Dispose();
        }
    }
}
