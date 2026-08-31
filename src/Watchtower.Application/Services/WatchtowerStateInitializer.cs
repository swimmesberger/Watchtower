using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.InternalCa;

namespace Watchtower.Application.Services;

/// <summary>
/// Brings the proxy/auth plane's in-memory state into line with the database, once, before anything is
/// served — ADR-0024.
/// </summary>
/// <remarks>
/// The ordering here is the whole point, which is why it is one named step rather than three calls
/// scattered through the host: the legacy file import has to run before either consumer reads its table
/// (or the first start after an upgrade would generate a signing key next to the one it was about to
/// import), the certificate store has to be full before Kestrel accepts a connection (an empty SNI map
/// fails handshakes rather than delaying them), and the signing key has to be loaded before the first
/// assertion is minted (so every instance stamps the same <c>kid</c> the JWKS advertises).
/// <para>
/// Called by the host after migrations, and by the test host after it builds its provider — the same
/// step in both, so a test can never be exercising a startup shape the deployment does not have.
/// </para>
/// </remarks>
public static class WatchtowerStateInitializer {
    /// <summary>
    /// Runs the file import and fills the certificate store and the token signer. Requires a service
    /// provider whose database is already migrated.
    /// </summary>
    public static async Task InitializeWatchtowerStateAsync(
        this IServiceProvider services, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(services);

        await using (var scope = services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<FileStateImport>().RunAsync(ct);

        await services.GetRequiredService<CertificateStore>().InitializeAsync(ct);
        // Immediately after the store is filled, and before anything is served: a LAN certificate that
        // is missing or no longer names what it should is issued here rather than a background pass
        // later, so the first connection after a restart already gets it. A no-op — not even a CA is
        // created — while nothing wants one.
        await services.GetRequiredService<InternalCertificateService>().EnsureAsync(ct);
        await services.GetRequiredService<AuthTokenSigner>().InitializeAsync(ct);
    }
}
