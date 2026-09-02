using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Watchtower.Api.Tests;

/// <summary>
/// <c>proxy.suggestLanNames</c> over the wire — the LAN-name chips the Settings page offers
/// (ADR-0033 decision 6).
/// </summary>
/// <remarks>
/// Through the JSON-RPC pipeline rather than against the handler directly, because the two things that
/// break silently are both on that path: the method name, and the <see cref="Application.Modules.Proxy.ProxyJsonContext"/>
/// entries without which every call answers "internal error" with the reason only in the log.
/// <para>
/// Only the DNS seam is substituted. The Docker source is left as it is and asserted around rather than
/// pinned, for the reason <c>MgmtApiTests</c> gives: <c>DockerEngineClient</c> hard-codes its socket
/// path, so whether a daemon answers depends on the machine — and the property under test here is one
/// that holds whatever the daemon says, since a value the setting already names is excluded no matter
/// which source found it. Its being fail-open is covered where it can be made deterministic, in
/// <c>LanNameSuggestionsTests</c>.
/// </para>
/// </remarks>
public sealed class ProxyLanNameSuggestionTests {
    [Fact]
    public async Task TheAddressTheBrowserUsed_IsOfferedAsTheNameItAnswersTo() {
        using var factory = ProxyHost("nas.lan");
        factory.Dns.ReverseTo("192.168.1.10", "nas.lan", "media.lan");
        using var client = factory.CreateApiClient();

        var candidates = await SuggestAsync(client, hint: "192.168.1.10");

        // The reverse name that is not already in the setting is offered…
        var candidate = Assert.Single(candidates, c => Value(c) == "media.lan");
        Assert.Equal("hostname", candidate.GetProperty("kind").GetString());
        Assert.Equal("reverse-dns", candidate.GetProperty("source").GetString());
        Assert.False(string.IsNullOrWhiteSpace(candidate.GetProperty("detail").GetString()));
        // …and the one that is, is not — whichever source found it.
        Assert.DoesNotContain("nas.lan", candidates.Select(Value));
    }

    /// <summary>
    /// The hint is the one address known to work, and the client offers it itself — so a server that
    /// echoed it back would produce a duplicate chip next to the certain one.
    /// </summary>
    [Fact]
    public async Task TheHintItself_IsNotAmongTheCandidates() {
        using var factory = ProxyHost(lanNames: "");
        factory.Dns.ReverseTo("192.168.1.10", "nas.lan");
        using var client = factory.CreateApiClient();

        var candidates = await SuggestAsync(client, hint: "192.168.1.10");

        Assert.Contains("nas.lan", candidates.Select(Value));
        Assert.DoesNotContain("192.168.1.10", candidates.Select(Value));
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
