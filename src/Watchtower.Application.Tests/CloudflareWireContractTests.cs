using System.Text.Json;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Pins what the Cloudflare <em>response</em> types promise, which is not what a reader would assume
/// from looking at them.
/// </summary>
/// <remarks>
/// <para>
/// `JsonSourceGenerator` treats a record whose members are all <c>init</c> properties as having a
/// parameterized constructor, and emits a creator that assigns <em>every</em> property from the parsed
/// arguments. A field the response omits therefore arrives as null, overwriting whatever the property
/// initializer would have produced — so a <c>= ""</c> on one of these types is not a default, it is a
/// promise the deserializer does not keep, and one the compiler stops checking at the use sites.
/// </para>
/// <para>
/// These tests exist so that stays a decision rather than a discovery. Each response property is
/// either <c>required</c> — Cloudflare always sends it and we would build a wrong URL or a wrong DNS
/// target without it, so an absent one must fail loudly — or nullable, because we only compare, log or
/// ignore it and failing a whole reconcile over a cosmetic field would be worse. There is no third
/// category, and in particular no <c>= ""</c>.
/// </para>
/// </remarks>
public sealed class CloudflareWireContractTests {
    // ── The load-bearing ids fail loudly ─────────────────────────────────────

    [Fact]
    public void AnAccessApplicationWithoutAnId_IsRefused() {
        // The id addresses the policy endpoints and the delete path. A null would send a policy write
        // to the account's whole application collection, so this must not deserialize at all.
        const string Response = """{"name":"watchtower: app.example.com","domain":"app.example.com"}""";
        var ex = Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize(Response, CloudflareJsonContext.Default.CloudflareAccessApp));
        // The outer message only says which type failed; the member that was missing is named by the
        // inner one, which is the half worth reading when this shows up in a log.
        Assert.Contains(nameof(CloudflareAccessApp.Id), ex.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ATunnelWithoutAnId_IsRefused() {
        // Every route's CNAME points at `{id}.cfargotunnel.com`. A null here would publish the whole
        // route table at `.cfargotunnel.com` with nothing to notice it.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("""{"name":"watchtower"}""", CloudflareJsonContext.Default.CloudflareTunnel));
    }

    [Fact]
    public void ADnsRecordWithoutAnId_IsRefused() {
        // `zones/{zone}/dns_records/{id}` — a null addresses the collection, turning the PUT that
        // meant to replace a record into one that creates another.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("""{"type":"CNAME","name":"app.example.com"}""",
                CloudflareJsonContext.Default.CloudflareDnsRecord));
    }

    [Fact]
    public void AZoneWithoutAnIdOrAName_IsRefused() {
        // Both are load-bearing: the name is what a hostname is matched against, the id is the answer
        // that match produces and the address of every DNS write that follows.
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("""{"name":"example.com","status":"active"}""",
                CloudflareJsonContext.Default.CloudflareZone));
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("""{"id":"z1","status":"active"}""",
                CloudflareJsonContext.Default.CloudflareZone));
    }

    [Fact]
    public void AnAccessPolicyWithoutAnId_IsRefused() {
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize("""{"name":"watchtower","decision":"allow"}""",
                CloudflareJsonContext.Default.CloudflareAccessPolicy));
    }

    // ── Everything else reads as null, and is declared that way ──────────────

    [Fact]
    public void AnApplicationResponse_CarriesItsAudienceTag() {
        // The AUD tag is what WATCHTOWER_AUTH_AUDIENCE injects, so the create/update response has to be
        // read for it rather than only for the id. Cloudflare spells it `aud`, a sibling of `id`.
        const string Response = """
            {"id":"a1","name":"watchtower: app.example.com","domain":"app.example.com",
             "type":"self_hosted","aud":"4714c1358e65fe6b4e3f2d9e0c1b8a75"}
            """;
        var app = JsonSerializer.Deserialize(Response, CloudflareJsonContext.Default.CloudflareAccessApp);
        Assert.NotNull(app);
        Assert.Equal("4714c1358e65fe6b4e3f2d9e0c1b8a75", app.Aud);
    }

    [Fact]
    public void AnApplicationsOptionalFields_ReadAsNull_NotAsEmptyStrings() {
        // The declaration each of these carries. `aud` absent means "not known", so the provider
        // records nothing and the route contributes no audience rather than a blank one.
        const string Response = """{"id":"a1"}""";
        var app = JsonSerializer.Deserialize(Response, CloudflareJsonContext.Default.CloudflareAccessApp);
        Assert.NotNull(app);
        Assert.Equal("a1", app.Id);
        Assert.Null(app.Name);
        Assert.Null(app.Domain);
        Assert.Null(app.Type);
        Assert.Null(app.Aud);
    }

    [Fact]
    public void ADnsRecordsOptionalFields_ReadAsNull() {
        var record = JsonSerializer.Deserialize("""{"id":"r1"}""",
            CloudflareJsonContext.Default.CloudflareDnsRecord);
        Assert.NotNull(record);
        Assert.Null(record.Type);
        Assert.Null(record.Name);
        Assert.Null(record.Content);
        Assert.Null(record.ZoneName);
        Assert.Null(record.Proxied);
    }

    [Fact]
    public void AnErrorResponse_NeverThrowsWhileReportingSomebodyElsesFailure() {
        // The one type that must deserialize whatever it is handed: it is only read to explain a call
        // that already failed, and a strictness that threw here would replace the message the operator
        // needs with a message about parsing the message.
        var envelope = JsonSerializer.Deserialize("""{"success":false,"errors":[{"code":10000}]}""",
            CloudflareJsonContext.Default.CloudflareEnvelopeJsonElement);
        Assert.NotNull(envelope);
        var error = Assert.Single(envelope.Errors!);
        Assert.Equal(10000, error.Code);
        Assert.Null(error.Message);
    }

    // ── The consequence the sweep depends on ─────────────────────────────────

    [Fact]
    public void AnApplicationWithNoName_IsNeverSweptAsStale() {
        // StaleApps deletes what carries the `watchtower: ` prefix. The prefix test is the only thing
        // between that sweep and an operator's dashboard-made applications, so a response that did not
        // say must fall on the side of leaving it alone — and must not throw on the way past, which is
        // what `app.Name.StartsWith(...)` used to do.
        var nameless = JsonSerializer.Deserialize("""{"id":"a1","domain":"app.example.com"}""",
            CloudflareJsonContext.Default.CloudflareAccessApp);
        Assert.NotNull(nameless);

        var projection = new CloudflareTunnelProvider.AccessProjection([], []);
        Assert.Empty(CloudflareTunnelProvider.StaleApps([nameless], projection));
    }
}
