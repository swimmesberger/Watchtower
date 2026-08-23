using Watchtower.Application.Config;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Pins how the stored <c>Proxy:Provider</c> string resolves to a backend. It is a free-text setting
/// that arrives from an environment variable as readily as from the Settings page, so the tolerant
/// parse (trimmed, case-insensitive) and the fallback matter: an unrecognised value must land on a
/// working provider rather than on nothing at all.
/// </summary>
public sealed class ProxyProviderResolutionTests {
    /// <summary>
    /// The default since ADR-0022. An upgrade never reaches this: an instance that was serving routes
    /// under the old implicit <c>caddy</c> default has the provider written into its settings store once
    /// by <c>ProxyProviderMigration</c>, so the value is stated by the time it is read here.
    /// </summary>
    [Fact]
    public void TheDefaultProviderIsTheInProcessProxy() {
        var options = new ProxyOptions();
        Assert.Equal(ProxyProviderKind.Yarp, options.ResolveProvider());
        Assert.Equal(ProxyProviderNames.Yarp, options.ProviderName());
    }

    [Theory]
    [InlineData("caddy")]
    [InlineData("CADDY")]
    [InlineData("  Caddy  ")]
    public void TheDeprecatedCaddyProviderStillResolves(string stored) {
        var options = new ProxyOptions { Provider = stored };
        Assert.Equal(ProxyProviderKind.Caddy, options.ResolveProvider());
        Assert.Equal(ProxyProviderNames.Caddy, options.ProviderName());
    }

    [Theory]
    [InlineData("yarp")]
    [InlineData("YARP")]
    [InlineData("  Yarp  ")]
    public void TheInProcessProviderIsRecognisedWhateverItsCasingOrPadding(string stored) {
        var options = new ProxyOptions { Provider = stored };
        Assert.Equal(ProxyProviderKind.Yarp, options.ResolveProvider());
        Assert.Equal(ProxyProviderNames.Yarp, options.ProviderName());
    }

    [Fact]
    public void CloudflareStillResolves() {
        var options = new ProxyOptions { Provider = "Cloudflare" };
        Assert.Equal(ProxyProviderKind.Cloudflare, options.ResolveProvider());
        Assert.Equal(ProxyProviderNames.Cloudflare, options.ProviderName());
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("")]
    [InlineData("   ")]
    public void AnUnrecognisedProviderFallsBackToTheDefault(string stored) {
        var options = new ProxyOptions { Provider = stored };
        Assert.Equal(ProxyProviderKind.Yarp, options.ResolveProvider());
        Assert.Equal(ProxyProviderNames.Yarp, options.ProviderName());
    }

    [Fact]
    public void EveryNameRoundTripsThroughItsKind() {
        foreach (var name in ProxyProviderNames.All) {
            var kind = new ProxyOptions { Provider = name }.ResolveProvider();
            Assert.Equal(name, ProxyProviderNames.From(kind));
        }
    }

    [Fact]
    public void EveryKindHasAName() =>
        Assert.All(
            Enum.GetValues<ProxyProviderKind>(),
            kind => Assert.Contains(ProxyProviderNames.From(kind), ProxyProviderNames.All));
}
