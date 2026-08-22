using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// The same issuance flow against a real ACME server — Pebble, or a step-ca — rather than the in-process
/// fake. Skipped unless <c>WATCHTOWER_PEBBLE_URL</c> names one.
/// </summary>
/// <remarks>
/// <see cref="FakeAcmeServer"/> is a reading of RFC 8555, and a reading can be wrong in the same way the
/// client is: both were written from the same understanding, so a shared misreading passes both. This is
/// the test that catches that class of error, which is why it exists despite needing a container.
/// <para>
/// Run it with Pebble's own strict mode, which deliberately varies the things the RFC leaves open — nonce
/// rejection rates, whether an order is returned already valid — precisely to break clients that assumed:
/// <code>
/// docker run --rm -p 14000:14000 -p 15000:15000 ghcr.io/letsencrypt/pebble:latest \
///   -config /test/config/pebble-config.json -strict
/// curl -sk https://localhost:15000/roots/0 &gt; /tmp/pebble-root.pem
/// WATCHTOWER_PEBBLE_URL=https://localhost:14000/dir WATCHTOWER_PEBBLE_CA=/tmp/pebble-root.pem \
///   dotnet test --filter PebbleAcmeSmokeTests
/// </code>
/// The domain has to be one Pebble can reach back on, which in practice means running Watchtower's HTTP
/// listener where Pebble's <c>httpPort</c> points — so this is a smoke test for a human, not for CI.
/// </para>
/// </remarks>
public sealed class PebbleAcmeSmokeTests {
    private static string? DirectoryUrl => Environment.GetEnvironmentVariable("WATCHTOWER_PEBBLE_URL");
    private static string? CaBundle => Environment.GetEnvironmentVariable("WATCHTOWER_PEBBLE_CA");

    /// <summary>The name to order for; Pebble validates over HTTP-01 against whatever it resolves.</summary>
    private static string Host =>
        Environment.GetEnvironmentVariable("WATCHTOWER_PEBBLE_HOST") ?? "localhost";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ACertificateIsIssuedByARealAcmeServer() {
        Assert.SkipUnless(
            !string.IsNullOrWhiteSpace(DirectoryUrl),
            "Set WATCHTOWER_PEBBLE_URL (and WATCHTOWER_PEBBLE_CA) to run this against a real ACME server.");

        using var factory = WatchtowerApiFactory.WithYarpProxy(
            ("Watchtower:Proxy:Yarp:AcmeDirectoryUrl", DirectoryUrl),
            ("Watchtower:Proxy:Yarp:AcmeCaBundlePath", CaBundle),
            // Pebble reaches this host from outside the process, so the loopback self-check would be
            // answering a different question than the one that matters.
            ("Watchtower:Proxy:Yarp:AcmeSelfCheckEnabled", "false"),
            ("Watchtower:Proxy:AdminEmail", "ops@example.invalid"));

        factory.Services.GetRequiredService<YarpListenerState>().HttpsBound = true;
        var certificates = factory.Services.GetRequiredService<CertificateManager>();
        certificates.SetDesiredHosts([Host]);

        var outcome = await certificates.RenewNowAsync(Host, Ct);

        var issued = Assert.IsType<IssueOutcome.Issued>(outcome);
        Assert.True(issued.NotAfter > DateTimeOffset.UtcNow);
        Assert.NotNull(factory.Services.GetRequiredService<CertificateStore>().SelectContext(Host));
    }
}
