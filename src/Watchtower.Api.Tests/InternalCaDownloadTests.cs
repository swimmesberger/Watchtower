using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.InternalCa;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// <c>GET /api/proxy/internal-ca.crt</c> — the one step of the LAN-HTTPS setup that happens outside
/// Watchtower: the operator downloads this root and imports it into a client's trust store. Both
/// encodings are served because the import dialogs disagree about which they accept.
/// </summary>
public sealed class InternalCaDownloadTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithNoCa_ThereIsNothingToDownload_AndAskingDoesNotCreateOne() {
        using var factory = new WatchtowerApiFactory();
        using var client = factory.CreateApiClient();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync(InternalCaNames.DownloadPath, Ct)).StatusCode);

        // A root that exists is a root an operator is invited to trust; a download must not be what
        // brings one into being.
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.False(await db.InternalCas.AnyAsync(Ct));
    }

    [Fact]
    public async Task TheRootIsServedAsPem_AsAnAttachment() {
        using var factory = new WatchtowerApiFactory();
        using var client = factory.CreateApiClient();
        var thumbprint = await CreateCaAsync(factory);

        var response = await client.GetAsync(InternalCaNames.DownloadPath, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/x-pem-file", response.Content.Headers.ContentType?.MediaType);
        // Offered as a file rather than rendered: a certificate shown in a browser tab is a page of
        // base64 nobody can import.
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Contains(
            InternalCaNames.DownloadFileName, response.Content.Headers.ContentDisposition?.FileName ?? "");

        var pem = await response.Content.ReadAsStringAsync(Ct);
        using var certificate = X509Certificate2.CreateFromPem(pem);
        Assert.Equal(thumbprint, certificate.Thumbprint);
        Assert.Equal("CN=Watchtower Internal CA", certificate.Subject);
        // The private key stays in the database — this is a trust anchor, not a credential.
        Assert.False(certificate.HasPrivateKey);
    }

    [Fact]
    public async Task TheRootIsAlsoServedAsDer() {
        using var factory = new WatchtowerApiFactory();
        using var client = factory.CreateApiClient();
        var thumbprint = await CreateCaAsync(factory);

        var response = await client.GetAsync($"{InternalCaNames.DownloadPath}?format=der", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pkix-cert", response.Content.Headers.ContentType?.MediaType);
        var der = await response.Content.ReadAsByteArrayAsync(Ct);
        using var certificate = X509CertificateLoader.LoadCertificate(der);
        Assert.Equal(thumbprint, certificate.Thumbprint);
    }

    /// <summary>The CA the way the issuance path creates it — nothing here writes rows by hand.</summary>
    private static async Task<string> CreateCaAsync(WatchtowerApiFactory factory) {
        using var root = await factory.Services.GetRequiredService<InternalCaStore>().LoadOrCreateAsync(Ct);
        return root.Thumbprint;
    }
}
