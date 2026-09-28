using Elarion.Abstractions;
using Elarion.Settings;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Config;
using Watchtower.Application.Modules.Proxy.Handlers;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The session duration Watchtower gives its Access applications. It is written onto every protected
/// hostname's app on every reconcile, so a value Cloudflare would reject has to be caught while it is
/// typed — at the edge it would fail each app write in turn and leave the hostnames as they were.
/// </summary>
public sealed class CloudflareAccessSessionDurationTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("24h")]
    [InlineData("30m")]
    [InlineData("730h")]
    [InlineData("2h45m")]
    [InlineData("1.5h")]
    [InlineData("0s")]
    [InlineData("300ms")]
    [InlineData("10µs")]
    public void ACloudflareDuration_IsAccepted(string value) =>
        Assert.True(CloudflareProxyOptions.IsAccessSessionDuration(value));

    [Theory]
    [InlineData("24")]        // No unit.
    [InlineData("1d")]        // Cloudflare has no day unit.
    [InlineData("24 h")]
    [InlineData("h")]
    [InlineData("-1h")]
    [InlineData("24h ago")]
    public void AnythingElse_IsNot(string value) =>
        Assert.False(CloudflareProxyOptions.IsAccessSessionDuration(value));

    [Theory]
    [InlineData("", "24h")]
    [InlineData("   ", "24h")]
    [InlineData(" 8h ", "8h")]
    // An env pin the save-time check never saw: sending it would fail every application write.
    [InlineData("one day", "24h")]
    public void TheResolvedDuration_FallsBackToTheDefault(string configured, string expected) =>
        Assert.Equal(expected,
            new CloudflareProxyOptions { AccessSessionDuration = configured }.ResolveAccessSessionDuration());

    [Theory]
    [InlineData("8h", "8h")]
    [InlineData(" 30m ", "30m")]
    [InlineData(null, "12h")]
    [InlineData("", "12h")]
    // A hand-edited row the save-time check never saw: the instance-wide value, not a rejected write.
    [InlineData("1d", "12h")]
    public void ARoutesOwnDuration_WinsOverTheInstanceWideOne(string? routeDuration, string expected) =>
        Assert.Equal(expected, CloudflareTunnelProvider.SessionDurationFor(routeDuration, "12h"));

    [Fact]
    public async Task AValidDuration_IsPersistedAndEchoed() {
        using var host = AuthTestHost.Start();

        var result = await SaveAsync(host, Command() with { CloudflareAccessSessionDuration = " 8h " });

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("8h", result.Value.Config.Cloudflare.AccessSessionDuration);
        var settings = host.Services.GetRequiredService<ISettingsManager>();
        Assert.Equal("8h", await settings.GetStringAsync(
            WatchtowerSettingPaths.ProxyCloudflareAccessSessionDuration, SettingsScope.Global, Ct));
    }

    /// <summary>Empty is how an operator goes back to the default, so it has to stay saveable.</summary>
    [Fact]
    public async Task AnEmptyDuration_IsAccepted() {
        using var host = AuthTestHost.Start();

        var result = await SaveAsync(host, Command() with { CloudflareAccessSessionDuration = "" });

        Assert.True(result.IsSuccess, Describe(result));
        Assert.Equal("", result.Value.Config.Cloudflare.AccessSessionDuration);
    }

    [Theory]
    [InlineData("1d")]
    [InlineData("24")]
    public async Task AnUnreadableDuration_IsRefused(string value) {
        using var host = AuthTestHost.Start();

        var result = await SaveAsync(host, Command() with { CloudflareAccessSessionDuration = value });

        Assert.False(result.IsSuccess);
        Assert.Contains("Access session duration", result.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>A client that predates the field omits it, and its save must neither fail nor clear the value.</summary>
    [Fact]
    public async Task AnOmittedDuration_KeepsTheStoredOne() {
        using var host = AuthTestHost.Start();
        Assert.True((await SaveAsync(host, Command() with { CloudflareAccessSessionDuration = "12h" })).IsSuccess);

        var result = await SaveAsync(host, Command());

        Assert.True(result.IsSuccess, Describe(result));
        var settings = host.Services.GetRequiredService<ISettingsManager>();
        Assert.Equal("12h", await settings.GetStringAsync(
            WatchtowerSettingPaths.ProxyCloudflareAccessSessionDuration, SettingsScope.Global, Ct));
    }

    // ── Wiring ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A disabled Caddy save: the duration is checked whichever provider is selected, and this one never
    /// reaches Cloudflare's API.
    /// </summary>
    private static UpdateProxyConfig.Command Command() =>
        new(Enabled: false, Provider: ProxyProviderNames.Caddy, AdminEmail: null, CaddyImage: "caddy:2");

    private static async Task<Result<UpdateProxyConfig.Response>> SaveAsync(
        AuthTestHost host, UpdateProxyConfig.Command command) {
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateProxyConfig>(scope.ServiceProvider);
        return await handler.HandleAsync(command, Ct);
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
