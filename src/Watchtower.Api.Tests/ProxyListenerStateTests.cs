using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// What the in-process proxy's host wiring does when there is no listener to speak of. Everything phase
/// 4 adds to <c>Program.cs</c> is conditional on a Kestrel endpoint the test host never configures, and
/// the point here is that "conditional" really means inert: the app still boots, the certificate store
/// still opens over its directory, and the proxy honestly reports that nothing is bound.
/// </summary>
public sealed class ProxyListenerStateTests {
    [Fact]
    public async Task UnderTestServer_TheAppStarts_AndReportsNoHttpsListener() {
        using var factory = new WatchtowerApiFactory();
        using var client = factory.CreateApiClient();
        var ct = TestContext.Current.CancellationToken;

        // The listener-state initializer runs on ApplicationStarted, which the first request forces.
        // TestServer exposes no address feature at all, and that has to be a shrug rather than a crash.
        var health = await client.GetAsync("/health", ct);
        Assert.True(health.IsSuccessStatusCode);

        var response = await client.PostAsJsonAsync(
            "/rpc",
            new { jsonrpc = "2.0", method = "proxy.getConfig", @params = new { }, id = "1" },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.DoesNotContain("\"error\"", body, StringComparison.Ordinal);
        Assert.Contains("\"httpsListenerBound\":false", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The store is constructible over the configured directory — the check that would otherwise only
    /// happen inside a container, since nothing in the test host resolves it on its own.
    /// </summary>
    [Fact]
    public void TheCertificateStore_OpensOverTheConfiguredDirectory() {
        using var factory = new WatchtowerApiFactory();

        var store = factory.Services.GetRequiredService<CertificateStore>();

        Assert.True(Directory.Exists(store.RootPath));
        Assert.Empty(store.Entries);
        Assert.Null(store.SelectContext("app.example.invalid"));
    }
}
