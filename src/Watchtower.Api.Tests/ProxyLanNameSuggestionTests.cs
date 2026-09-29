using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// <c>proxy.suggestLanNames</c> over the wire — the LAN-name chips the Settings page offers for the LAN
/// names setting of ADR-0033 decision 6.
/// </summary>
/// <remarks>
/// Through the JSON-RPC pipeline rather than against the handler directly, because the two things that
/// break silently are both on that path: the method name, and the <see cref="Application.Modules.Proxy.ProxyJsonContext"/>
/// entries without which every call answers "internal error" with the reason only in the log.
/// <para>
/// Only the DNS seam is substituted. The Docker source is left as it is and asserted around rather than
/// pinned, for the reason <c>MgmtApiTests</c> gives: <c>DockerEngineClient</c> hard-codes its socket
/// path, so whether a daemon answers depends on the machine — and the properties under test here hold
/// whatever the daemon says, since a value already in the setting is excluded no matter which source
/// found it. The rules themselves are covered where they can be made deterministic, in
/// <c>LanNameSuggestionsTests</c>.
/// </para>
/// </remarks>
public sealed class ProxyLanNameSuggestionTests {
    /// <summary>
    /// The address the browser arrived on leads the list, because it is the one the operator is looking
    /// at — and it is the server that says so, rather than a client synthesising a chip of its own.
    /// </summary>
    [Fact]
    public async Task TheAddressTheBrowserUsed_LeadsAndItsReverseNameFollows() {
        using var factory = ProxyHost(lanNames: "");
        factory.Dns.ReverseTo("192.168.1.10", "nas.lan", "media.lan");
        using var client = factory.CreateApiClient();

        var candidates = await SuggestAsync(client, hint: "192.168.1.10");

        Assert.Equal("192.168.1.10", Value(candidates[0]));
        Assert.Equal("browser", candidates[0].GetProperty("source").GetString());
        Assert.Equal("ip", candidates[0].GetProperty("kind").GetString());
        Assert.True(candidates[0].GetProperty("verified").GetBoolean());

        var name = Assert.Single(candidates, c => Value(c) == "media.lan");
        Assert.Equal("hostname", name.GetProperty("kind").GetString());
        Assert.Equal("reverse-dns", name.GetProperty("source").GetString());
        Assert.False(string.IsNullOrWhiteSpace(name.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task WhatTheSettingAlreadyHolds_IsNotOfferedAgain() {
        using var factory = ProxyHost(lanNames: "nas.lan, 192.168.1.10");
        factory.Dns.ReverseTo("192.168.1.10", "nas.lan", "media.lan");
        using var client = factory.CreateApiClient();

        var candidates = await SuggestAsync(client, hint: "192.168.1.10");

        // Both configured values are gone — the hint included, which is what makes a clicked chip
        // disappear instead of coming back on the next refetch.
        Assert.DoesNotContain("nas.lan", candidates.Select(Value));
        Assert.DoesNotContain("192.168.1.10", candidates.Select(Value));
        Assert.Contains("media.lan", candidates.Select(Value));
    }

    /// <summary>
    /// A browser can hold an address a certificate cannot name. The client renders what it is sent
    /// without judging it, so the judging happens here: an excluded or unparseable hint is silently
    /// absent rather than offered as a chip whose click makes the Save fail.
    /// </summary>
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("host.docker.internal")]
    [InlineData("my_nas")]
    public async Task AHintTheCertificateCouldNotName_IsSilentlyAbsent(string hint) {
        using var factory = ProxyHost(lanNames: "");
        using var client = factory.CreateApiClient();

        var candidates = await SuggestAsync(client, hint);

        Assert.DoesNotContain(hint, candidates.Select(Value));
    }

    /// <summary>
    /// No hint at all — a client that could not read its own location — is answered, not refused. The
    /// call is a convenience; a validation error under the field would be worse than no chips.
    /// </summary>
    [Fact]
    public async Task NoHint_IsStillAnAnswer() {
        using var factory = ProxyHost(lanNames: "");
        using var client = factory.CreateApiClient();

        var response = await client.PostAsJsonAsync(
            "/rpc",
            new { jsonrpc = "2.0", method = "proxy.suggestLanNames", @params = new { }, id = "1" },
            TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain("\"error\"", body, StringComparison.Ordinal);
        Assert.Contains("\"candidates\"", body, StringComparison.Ordinal);
    }

    private static WatchtowerApiFactory ProxyHost(string lanNames) => new([
        ("Watchtower:Proxy:Enabled", "true"),
        ("Watchtower:Proxy:Provider", "yarp"),
        ("Watchtower:Proxy:PortRoutes:LanNames", lanNames),
    ]);

    private static string? Value(JsonElement candidate) => candidate.GetProperty("value").GetString();

    private static async Task<IReadOnlyList<JsonElement>> SuggestAsync(HttpClient client, string hint) {
        var ct = TestContext.Current.CancellationToken;
        var response = await client.PostAsJsonAsync(
            "/rpc",
            new { jsonrpc = "2.0", method = "proxy.suggestLanNames", @params = new { hint }, id = "1" },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.DoesNotContain("\"error\"", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        return [.. document.RootElement.GetProperty("result").GetProperty("candidates").EnumerateArray()
            .Select(c => c.Clone())];
    }
}
