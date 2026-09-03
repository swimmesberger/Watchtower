using System.Text.Json;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Pins the JSON Cloudflare actually receives for the two request shapes ADR-0035 introduced: an
/// application that covers a route's public paths, and a policy whose decision applies to everybody.
/// </summary>
/// <remarks>
/// Worth asserting because neither shape is expressible in C# the way Cloudflare spells it. The public
/// paths travel as <c>destinations</c> objects — the <c>self_hosted_domains</c> string array they replace
/// went out of support on 21 November 2025 — and the "everyone" include rule is an empty object, which is
/// a record with no members here and would silently serialize as <c>null</c> if it ever gained one.
/// </remarks>
public sealed class CloudflareAccessWireTests {
    [Fact]
    public void ABypassApplication_SendsItsPublicPathsAsDestinations() {
        var request = new CloudflareAccessAppRequest {
            Name = "watchtower: app.example.com (public paths)",
            Domain = "app.example.com/healthz",
            Type = "self_hosted",
            SessionDuration = "24h",
            AppLauncherVisible = false,
            Destinations = [
                CloudflareAccessDestination.Public("app.example.com/healthz"),
                CloudflareAccessDestination.Public("app.example.com/webhooks"),
            ],
        };

        using var json = JsonDocument.Parse(Serialize(request));
        var destinations = json.RootElement.GetProperty("destinations");
        Assert.Equal(2, destinations.GetArrayLength());
        Assert.Equal("public", destinations[0].GetProperty("type").GetString());
        Assert.Equal("app.example.com/healthz", destinations[0].GetProperty("uri").GetString());
        // The deprecated spelling must not appear alongside it: Cloudflare ignores it when destinations
        // are present, and sending both would only invite the two to drift apart.
        Assert.False(json.RootElement.TryGetProperty("self_hosted_domains", out _));
        // Omitted rather than null, so an update leaves any dashboard-attached policies alone.
        Assert.False(json.RootElement.TryGetProperty("policies", out _));
    }

    [Fact]
    public void AnOrdinaryApplication_SendsNoDestinationsAtAll() {
        var request = new CloudflareAccessAppRequest {
            Name = "watchtower: app.example.com",
            Domain = "app.example.com",
            Type = "self_hosted",
            SessionDuration = "24h",
            AppLauncherVisible = false,
        };

        using var json = JsonDocument.Parse(Serialize(request));
        Assert.False(json.RootElement.TryGetProperty("destinations", out _));
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("bypass")]
    public void APolicyAboutEverybody_SendsAnEmptyEveryoneRule(string decision) {
        var request = new CloudflareAccessPolicyRequest {
            Name = "watchtower",
            Decision = decision,
            Include = [CloudflareAccessRule.ForEveryone()],
            Precedence = 1,
        };

        using var json = JsonDocument.Parse(Serialize(request));
        Assert.Equal(decision, json.RootElement.GetProperty("decision").GetString());
        var rule = json.RootElement.GetProperty("include")[0];
        Assert.Equal(JsonValueKind.Object, rule.GetProperty("everyone").ValueKind);
        Assert.Empty(rule.GetProperty("everyone").EnumerateObject());
        // Cloudflare's rule objects are single-key unions: the unset members must not ride along as nulls.
        Assert.Single(rule.EnumerateObject());
    }

    private static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, typeof(T), CloudflareJsonContext.Default);
}
