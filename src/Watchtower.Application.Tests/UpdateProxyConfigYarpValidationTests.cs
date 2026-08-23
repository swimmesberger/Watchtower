using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Elarion.Abstractions;
using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Config;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Yarp;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <c>proxy.updateConfig</c>'s in-process-proxy half: the values it refuses, what it persists,
/// and what it says in the audit trail. These settings are validated on the way in because the plane
/// that consumes them runs in the background — a bad ACME URL or a half-configured EAB pair would
/// otherwise surface as a reconcile warning nobody is watching, weeks after it was typed.
/// </summary>
public sealed class UpdateProxyConfigYarpValidationTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheInProcessProviderIsAnAcceptedChoice() {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with { Provider = "  YARP  " });

        Assert.True(result.IsSuccess);
        Assert.Equal(ProxyProviderNames.Yarp, result.Value.Config.Provider);
        var settings = host.Services.GetRequiredService<ISettingsManager>();
        Assert.Equal("yarp", await settings.GetStringAsync(WatchtowerSettingPaths.ProxyProvider, SettingsScope.Global, Ct));
    }

    [Fact]
    public async Task AnUnknownProvider_ListsTheRealOnes() {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with { Provider = "nginx" });

        Assert.False(result.IsSuccess);
        Assert.Equal("Provider must be one of: yarp, caddy, cloudflare.", result.Error.Message);
    }

    [Theory]
    [InlineData("acme-v02.api.letsencrypt.org/directory")]           // Not absolute.
    [InlineData("ftp://acme.example.invalid/directory")]             // Not an HTTP(S) scheme.
    [InlineData("http://acme.example.invalid/directory")]            // Plaintext ACME over the network.
    public async Task ARefusedAcmeDirectoryUrl(string url) {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with { YarpAcmeDirectoryUrl = url });

        Assert.False(result.IsSuccess);
        Assert.Contains("absolute https URL", result.Error.Message);
    }

    [Theory]
    [InlineData("https://acme-staging-v02.api.letsencrypt.org/directory")]
    [InlineData("http://localhost:14000/dir")]
    [InlineData("http://127.0.0.1:14000/dir")]
    [InlineData("http://[::1]:14000/dir")]
    public async Task AnAcceptedAcmeDirectoryUrl(string url) {
        // Plaintext is fine against a loopback address: that is a local pebble/step-ca, not a
        // credential crossing the network.
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with { YarpAcmeDirectoryUrl = url });

        Assert.True(result.IsSuccess);
        Assert.Equal(url, result.Value.Config.Yarp.AcmeDirectoryUrl);
    }

    [Fact]
    public async Task ACaBundleThatIsNotThere_IsRefused() {
        using var host = AuthTestHost.Start();
        var missing = Path.Combine(Path.GetTempPath(), $"watchtower-{Guid.NewGuid():N}.pem");
        var result = await SaveAsync(host, Command() with { YarpAcmeCaBundlePath = missing });

        Assert.False(result.IsSuccess);
        Assert.Contains("was not found", result.Error.Message);
    }

    [Theory]
    [InlineData(false, ProxyProviderNames.Caddy)]      // Disabling the proxy outright.
    [InlineData(true, ProxyProviderNames.Caddy)]       // Switching away to the container-based provider.
    [InlineData(false, ProxyProviderNames.Yarp)]       // Turning the in-process provider off.
    public async Task AStaleStoredCaBundle_DoesNotBlockDisablingOrSwitchingAway(bool enabled, string provider) {
        // The escape hatch: a CA bundle that vanished (a remount, a rotated secret mount) is exactly the
        // situation an operator digs themselves out of by disabling the proxy or going back to caddy.
        // Validating the stored value on every save would wedge them out of both.
        using var host = StaleBundleHost(out _);
        var result = await SaveAsync(host, Command() with { Enabled = enabled, Provider = provider });

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task AStaleStoredCaBundle_IsRefusedWhenTheInProcessProviderIsSwitchedOn() {
        // Turning it on is the moment the value starts being acted on, so this is where it has to fail
        // — with the operator watching, not later in a background reconcile.
        using var host = StaleBundleHost(out var missing);
        var result = await SaveAsync(host, Command() with { Enabled = true, Provider = ProxyProviderNames.Yarp });

        Assert.False(result.IsSuccess);
        Assert.Contains("was not found", result.Error.Message);
        Assert.Contains(missing, result.Error.Message);
    }

    [Fact]
    public async Task AStaleStoredEabPair_IsRefusedWhenTheInProcessProviderIsSwitchedOn() {
        using var host = AuthTestHost.Start(("Watchtower:Proxy:Yarp:AcmeEabKeyId", "kid-1"));
        var result = await SaveAsync(host, Command() with { Enabled = true, Provider = ProxyProviderNames.Yarp });

        Assert.False(result.IsSuccess);
        Assert.Contains("must be set together", result.Error.Message);
    }

    [Fact]
    public async Task ARelativeCaBundlePath_IsRefused() {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with { YarpAcmeCaBundlePath = "certs/roots.pem" });

        Assert.False(result.IsSuccess);
        Assert.Contains("must be absolute", result.Error.Message);
    }

    [Fact]
    public async Task ACaBundleThatIsNotPem_ReportsTheParseError() {
        using var host = AuthTestHost.Start();
        var path = WriteTempFile("this is not a certificate");
        try {
            var result = await SaveAsync(host, Command() with { YarpAcmeCaBundlePath = path });

            Assert.False(result.IsSuccess);
            // The message the operator can act on is the parser's, not a paraphrase of it.
            Assert.StartsWith("The ACME CA bundle", result.Error.Message);
        } finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ARealCaBundle_IsAccepted() {
        using var host = AuthTestHost.Start();
        var path = WriteTempFile(SelfSignedPem());
        try {
            var result = await SaveAsync(host, Command() with { YarpAcmeCaBundlePath = path });

            Assert.True(result.IsSuccess);
            Assert.Equal(path, result.Value.Config.Yarp.AcmeCaBundlePath);
        } finally {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("kid-1", null)]
    [InlineData(null, "c2VjcmV0")]
    public async Task HalfAnEabPair_IsRefused(string? keyId, string? hmac) {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with { YarpAcmeEabKeyId = keyId, YarpAcmeEabHmacKey = hmac });

        Assert.False(result.IsSuccess);
        Assert.Contains("must be set together", result.Error.Message);
    }

    [Fact]
    public async Task AnEabHmacKeyThatIsNotBase64Url_IsRefused() {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with {
            YarpAcmeEabKeyId = "kid-1", YarpAcmeEabHmacKey = "not base64url!!",
        });

        Assert.False(result.IsSuccess);
        Assert.Contains("base64url", result.Error.Message);
    }

    [Fact]
    public async Task AWholeEabPair_IsStored_AndTheSecretIsOnlyEverAFlag() {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with {
            Provider = ProxyProviderNames.Yarp, YarpAcmeEabKeyId = "kid-1", YarpAcmeEabHmacKey = "c2VjcmV0LWtleQ",
        });

        Assert.True(result.IsSuccess);
        Assert.Equal("kid-1", result.Value.Config.Yarp.AcmeEabKeyId);
        Assert.True(result.Value.Config.Yarp.HasAcmeEabHmacKey);
        // The DTO is what the Settings page renders — the key itself must not be reachable from it.
        Assert.DoesNotContain("c2VjcmV0LWtleQ", System.Text.Json.JsonSerializer.Serialize(result.Value.Config));

        var settings = host.Services.GetRequiredService<ISettingsManager>();
        Assert.Equal("c2VjcmV0LWtleQ",
            await settings.GetStringAsync(WatchtowerSettingPaths.ProxyYarpAcmeEabHmacKey, SettingsScope.Global, Ct));
    }

    [Fact]
    public async Task TheCertPathIsNeverWrittenAtRuntime() {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with { Provider = ProxyProviderNames.Yarp });

        Assert.True(result.IsSuccess);
        // It is read at bind time, so a stored row would do nothing until the next restart — but it is
        // still reported, and still listed among the paths the card manages so a pin can disable it.
        var settings = host.Services.GetRequiredService<ISettingsManager>();
        Assert.Null(await settings.GetStringAsync(WatchtowerSettingPaths.ProxyYarpCertPath, SettingsScope.Global, Ct));
        Assert.NotEmpty(result.Value.Config.Yarp.CertPath);
        Assert.Contains(WatchtowerSettingPaths.ProxyYarpCertPath, GetProxyConfig.ProxyPaths);
    }

    [Fact]
    public async Task SavingTheInProcessProvider_RecordsTheCaAndTheSecretUpdate() {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with {
            Provider = ProxyProviderNames.Yarp,
            YarpAcmeDirectoryUrl = "https://acme-staging-v02.api.letsencrypt.org/directory",
            YarpAcmeEabKeyId = "kid-1",
            YarpAcmeEabHmacKey = "c2VjcmV0LWtleQ",
        });
        Assert.True(result.IsSuccess);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.SingleAsync(Ct);
        Assert.Equal("proxy", row.Category);
        Assert.Equal("config.update", row.Action);
        Assert.Contains("provider yarp", row.Detail);
        Assert.Contains("acme acme-staging-v02.api.letsencrypt.org", row.Detail);
        Assert.Contains("secrets updated: ACME EAB HMAC key", row.Detail);
        // The trail says a secret changed; it never says what to.
        Assert.DoesNotContain("c2VjcmV0LWtleQ", row.Detail);
    }

    [Fact]
    public async Task OmittedFieldsAreNeverWritten() {
        using var host = AuthTestHost.Start();
        var first = await SaveAsync(host, Command() with {
            YarpAcmeEabKeyId = "kid-1", YarpAcmeEabHmacKey = "c2VjcmV0LWtleQ", YarpRedirectHttpToHttps = false,
        });
        Assert.True(first.IsSuccess);

        // A save from a UI that never echoes the secret must leave the stored rows alone rather than
        // blanking them — which is what makes the token/key fields safe to render as empty.
        var second = await SaveAsync(host, Command());
        Assert.True(second.IsSuccess);

        var settings = host.Services.GetRequiredService<ISettingsManager>();
        Assert.Equal("kid-1",
            await settings.GetStringAsync(WatchtowerSettingPaths.ProxyYarpAcmeEabKeyId, SettingsScope.Global, Ct));
        Assert.Equal("c2VjcmV0LWtleQ",
            await settings.GetStringAsync(WatchtowerSettingPaths.ProxyYarpAcmeEabHmacKey, SettingsScope.Global, Ct));
        Assert.Equal("false",
            await settings.GetStringAsync(WatchtowerSettingPaths.ProxyYarpRedirectHttpToHttps, SettingsScope.Global, Ct));
    }

    // ── Ingress ports ────────────────────────────────────────────────────────

    /// <summary>
    /// The ports are ordinary runtime settings now — a save moves the listener, no restart. Checked at
    /// the boundary because the thing that acts on them is Kestrel's endpoint reload, where a bad value
    /// surfaces as a listener that quietly failed to bind.
    /// </summary>
    [Fact]
    public async Task TheIngressPortsArePersisted() {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with { YarpHttpPort = 18081, YarpHttpsPort = 18443 });

        Assert.True(result.IsSuccess);
        Assert.Equal(18081, result.Value.Config.Yarp.HttpPort);
        Assert.Equal(18443, result.Value.Config.Yarp.HttpsPort);
        var settings = host.Services.GetRequiredService<ISettingsManager>();
        Assert.Equal("18081",
            await settings.GetStringAsync(WatchtowerSettingPaths.ProxyYarpHttpPort, SettingsScope.Global, Ct));
        Assert.Equal("18443",
            await settings.GetStringAsync(WatchtowerSettingPaths.ProxyYarpHttpsPort, SettingsScope.Global, Ct));
    }

    /// <summary>Zero is a real answer — "do not bind that listener" — and has to survive the range check.</summary>
    [Fact]
    public async Task PortZeroTurnsAListenerOff() {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with { YarpHttpsPort = 0 });

        Assert.True(result.IsSuccess);
        Assert.Equal(0, result.Value.Config.Yarp.HttpsPort);
    }

    [Theory]
    [InlineData(-1, 8443, "between 1 and 65535")]
    [InlineData(8081, 70000, "between 1 and 65535")]
    [InlineData(8443, 8443, "must differ")]
    public async Task ARefusedPortPair(int http, int https, string expected) {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with { YarpHttpPort = http, YarpHttpsPort = https });

        Assert.False(result.IsSuccess);
        Assert.Contains(expected, result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>Both off is not a collision — it is a deployment with no ingress at all.</summary>
    [Fact]
    public async Task BothPortsOff_IsNotACollision() {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with { YarpHttpPort = 0, YarpHttpsPort = 0 });

        Assert.True(result.IsSuccess);
    }

    /// <summary>
    /// Taking the management port would put public ingress on the listener Watchtower's own UI is served
    /// on — and take the UI down with it, from inside the page that made the change.
    /// </summary>
    [Fact]
    public async Task AnIngressPortMayNotBeTheManagementPort() {
        using var host = AuthTestHost.Start();
        var listener = new YarpListenerState();
        listener.Publish(new YarpListenerSnapshot { ManagementPort = 8080 });

        var result = await SaveAsync(host, Command() with { YarpHttpPort = 8080 }, listener);

        Assert.False(result.IsSuccess);
        Assert.Contains("must not be the management port (8080)", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Enabling the in-process provider is the moment the stored ports start being acted on, so they are
    /// checked then too — even though this request supplied neither.
    /// </summary>
    [Fact]
    public async Task EnablingTheProvider_ChecksTheStoredPorts() {
        using var host = AuthTestHost.Start(("Watchtower:Proxy:Yarp:HttpsPort", "8081"));
        var result = await SaveAsync(
            host, Command() with { Enabled = true, Provider = ProxyProviderNames.Yarp });

        Assert.False(result.IsSuccess);
        Assert.Contains("must differ", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>A pinned port is reported, not written — env is the infrastructure-as-code layer.</summary>
    [Fact]
    public async Task APinnedPortIsRefused() {
        using var host = AuthTestHost.Start();
        var pins = new EnvironmentSettingPins(["WATCHTOWER__PROXY__YARP__HTTPSPORT"]);

        var result = await SaveAsync(host, Command() with { YarpHttpsPort = 18443 }, pins: pins);

        Assert.False(result.IsSuccess);
        Assert.Contains("WATCHTOWER__PROXY__YARP__HTTPSPORT", result.Error.Message, StringComparison.Ordinal);
        Assert.Contains(WatchtowerSettingPaths.ProxyYarpHttpsPort, GetProxyConfig.ProxyPaths);
    }

    /// <summary>The audit line names the ports, because changing one moves a listener facing the internet.</summary>
    [Fact]
    public async Task TheAuditLineNamesTheIngressPorts() {
        using var host = AuthTestHost.Start();
        var result = await SaveAsync(host, Command() with {
            Provider = ProxyProviderNames.Yarp, YarpHttpPort = 18081, YarpHttpsPort = 0,
        });
        Assert.True(result.IsSuccess);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var row = await db.AuditEvents.SingleAsync(Ct);
        Assert.Contains("ingress http 18081, https off", row.Detail, StringComparison.Ordinal);
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    private static UpdateProxyConfig.Command Command() =>
        new(Enabled: false, Provider: ProxyProviderNames.Caddy, AdminEmail: null, CaddyImage: "caddy:2");

    private static async Task<Result<UpdateProxyConfig.Response>> SaveAsync(
        AuthTestHost host,
        UpdateProxyConfig.Command command,
        YarpListenerState? listener = null,
        EnvironmentSettingPins? pins = null) {
        await using var scope = host.Services.CreateAsyncScope();
        object[] overrides = [.. new object?[] { listener, pins }.OfType<object>()];
        var handler = ActivatorUtilities.CreateInstance<UpdateProxyConfig>(scope.ServiceProvider, overrides);
        return await handler.HandleAsync(command, Ct);
    }

    /// <summary>A host whose stored CA bundle path points at a file that is no longer there.</summary>
    private static AuthTestHost StaleBundleHost(out string missingPath) {
        missingPath = Path.Combine(Path.GetTempPath(), $"watchtower-{Guid.NewGuid():N}.pem");
        return AuthTestHost.Start(("Watchtower:Proxy:Yarp:AcmeCaBundlePath", missingPath));
    }

    private static string WriteTempFile(string content) {
        var path = Path.Combine(Path.GetTempPath(), $"watchtower-{Guid.NewGuid():N}.pem");
        File.WriteAllText(path, content);
        return path;
    }

    private static string SelfSignedPem() {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=watchtower-test-root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return certificate.ExportCertificatePem();
    }
}
