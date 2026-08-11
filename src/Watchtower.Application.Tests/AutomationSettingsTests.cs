using Elarion.Settings;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Modules.System.Handlers;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the runtime-editable automation toggles (<c>system.getAutomation</c> /
/// <c>system.updateAutomation</c>) and the dangling-image prune they drive.
/// </summary>
/// <remarks>
/// The prune's blast radius lives entirely in one URL: <c>dangling=true</c> removes untagged layers,
/// <c>dangling=false</c> is <c>docker image prune -a</c> and would delete every tagged image no
/// container happens to be running — including the previous version of each stack. That is worth a
/// test that reads the request the client actually builds rather than the intent of the code.
/// </remarks>
public sealed class AutomationSettingsTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ── Settings round-trip ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAutomation_PersistsEveryToggle_AndEchoesThemBack() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<UpdateAutomation>(scope.ServiceProvider);

        var result = await handler.HandleAsync(
            new UpdateAutomation.Command(
                AutoCheckEnabled: true,
                AutoCheckIntervalMinutes: 5,
                StackCheckEnabled: true,
                StackCheckIntervalMinutes: 15,
                ImagePruneEnabled: true,
                ImagePruneIntervalMinutes: 720),
            Ct);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.ImagePruneEnabled);
        Assert.Equal(720, result.Value.ImagePruneIntervalMinutes);

        // The echo is only immediately-consistent sugar; what the background services eventually read
        // is the persisted Global-scope setting.
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        Assert.Equal("true", await settings.GetStringAsync("Watchtower:ImagePruneEnabled", SettingsScope.Global, Ct));
        Assert.Equal("720", await settings.GetStringAsync("Watchtower:ImagePruneIntervalMinutes", SettingsScope.Global, Ct));
        Assert.Equal("true", await settings.GetStringAsync("Watchtower:StackCheckEnabled", SettingsScope.Global, Ct));
    }

    [Fact]
    public async Task GetAutomation_DefaultsToPruningOffOnceADay() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<GetAutomation>(scope.ServiceProvider);

        var result = await handler.HandleAsync(new GetAutomation.Query(), Ct);

        Assert.True(result.IsSuccess);
        // Nothing is deleted from the host unless the operator opts in.
        Assert.False(result.Value.ImagePruneEnabled);
        Assert.Equal(1440, result.Value.ImagePruneIntervalMinutes);
    }

    [Fact]
    public async Task GetAutomation_ReflectsConfiguredValues() {
        using var host = AuthTestHost.Start(
            ("Watchtower:ImagePruneEnabled", "true"),
            ("Watchtower:ImagePruneIntervalMinutes", "60"));
        await using var scope = host.Services.CreateAsyncScope();
        var handler = ActivatorUtilities.CreateInstance<GetAutomation>(scope.ServiceProvider);

        var result = await handler.HandleAsync(new GetAutomation.Query(), Ct);

        Assert.True(result.IsSuccess);
        Assert.True(result.Value.ImagePruneEnabled);
        Assert.Equal(60, result.Value.ImagePruneIntervalMinutes);
    }

    // ── The prune request ─────────────────────────────────────────────────────

    [Fact]
    public void ThePruneCallFiltersToDanglingImagesOnly() {
        var url = DockerEngineClient.BuildImagePruneUrl("/v1.43");

        // Filters travel as a URL-encoded JSON object in the `filters` query parameter.
        Assert.Equal("/v1.43/images/prune?filters=%7B%22dangling%22%3A%5B%22true%22%5D%7D", url);
        // Decoded, the filter says dangling-only — never the "all unused" variant, whose only
        // textual difference from this one is the word `false`.
        Assert.Equal("""{"dangling":["true"]}""", Uri.UnescapeDataString(url.Split("filters=")[1]));
    }
}
