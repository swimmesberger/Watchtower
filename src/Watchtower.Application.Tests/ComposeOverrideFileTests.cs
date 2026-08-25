using System.Text.Json;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="ComposeOverrideFile"/> — reading the services out of
/// <c>docker compose config --format json</c>, and turning an <see cref="EnvInjectionPlan"/> back into
/// a Compose file.
/// </summary>
/// <remarks>
/// Both halves are string handling around a value that Compose will parse twice: once as YAML and once
/// more for <c>$</c> interpolation. A value that survives the first pass and not the second reaches the
/// container silently mangled, so the escaping cases are the point of this file.
/// </remarks>
public sealed class ComposeOverrideFileTests {
    // ── config --format json parsing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Shaped after real <c>docker compose config --format json</c> output: labels normalized to a map,
    /// services carrying plenty that is none of Watchtower's business.
    /// </summary>
    private const string ConfigJson = """
        {
          "name": "shop",
          "services": {
            "web": {
              "image": "ghcr.io/example/web:latest",
              "environment": { "DATABASE_PASSWORD": "hunter2" },
              "labels": {
                "com.example.team": "platform",
                "watchtower.inject-token": "true",
                "watchtower.release-image": "true"
              },
              "networks": { "default": null }
            },
            "worker": {
              "image": "ghcr.io/example/worker:latest",
              "labels": { "watchtower.inject-token": "false" }
            },
            "db": {
              "image": "postgres:17"
            }
          },
          "networks": { "default": { "name": "shop_default" } }
        }
        """;

    /// <summary>
    /// One read of the resolved project carries everything two policies decide from: the injection
    /// label, and — for image pinning — the resolved image and its own label, both raw.
    /// </summary>
    [Fact]
    public void ParseServices_ReadsEveryServiceWithItsImageAndBothLabelValues() {
        var services = ComposeOverrideFile.ParseServices(ConfigJson);

        Assert.Equal(
            [new EnvInjectionService("web", "true", "ghcr.io/example/web:latest", "true"),
             new EnvInjectionService("worker", "false", "ghcr.io/example/worker:latest"),
             new EnvInjectionService("db", null, "postgres:17")],
            services);
    }

    /// <summary>A build-only service declares no image; it is not an error, it is simply not pinnable.</summary>
    [Fact]
    public void ParseServices_ToleratesAServiceWithNoImage() {
        const string json = """
            {
              "services": {
                "web": { "build": { "context": "." }, "labels": { "watchtower.release-image": "true" } }
              }
            }
            """;

        Assert.Equal(
            [new EnvInjectionService("web", null, null, "true")],
            ComposeOverrideFile.ParseServices(json));
    }

    /// <summary>
    /// Both labels are read out of the <c>KEY=VALUE</c> list form too, and a bare key — Compose's "take
    /// it from the environment" syntax — carries no value, so it reads as absent rather than as an
    /// empty (and therefore unusable) one.
    /// </summary>
    [Fact]
    public void ParseServices_ReadsALabelCarriedAsAList() {
        const string json = """
            {
              "services": {
                "web": { "labels": ["com.example.team=platform", "watchtower.inject-token=true"] },
                "worker": { "labels": ["watchtower.inject-token"] },
                "jobs": { "labels": ["watchtower.release-image=false", "watchtower.inject-token=false"] }
              }
            }
            """;

        Assert.Equal(
            [new EnvInjectionService("web", "true"),
             new EnvInjectionService("worker"),
             new EnvInjectionService("jobs", "false", null, "false")],
            ComposeOverrideFile.ParseServices(json));
    }

    /// <summary>An unquoted <c>true</c> in YAML can come back as a JSON boolean rather than a string.</summary>
    [Fact]
    public void ParseServices_ReadsALabelThatWasNotQuoted() {
        const string json = """
            {
              "services": {
                "web": {
                  "labels": { "watchtower.inject-token": true, "watchtower.release-image": false }
                }
              }
            }
            """;

        Assert.Equal(
            [new EnvInjectionService("web", "true", null, "false")],
            ComposeOverrideFile.ParseServices(json));
    }

    [Theory]
    [InlineData("""{ "name": "empty" }""")]
    [InlineData("""{ "services": {} }""")]
    [InlineData("""{ "services": null }""")]
    [InlineData("[]")]
    public void ParseServices_ReturnsNothingForADocumentDeclaringNoServices(string json) =>
        Assert.Empty(ComposeOverrideFile.ParseServices(json));

    /// <summary>
    /// Nothing salvageable is attempted: a successful <c>config</c> that did not return JSON is a
    /// contradiction, and the deploy fails rather than proceeding with an empty service list — which
    /// would look exactly like a project that injects nothing.
    /// </summary>
    [Fact]
    public void ParseServices_RejectsOutputThatIsNotJson() =>
        Assert.ThrowsAny<JsonException>(() => ComposeOverrideFile.ParseServices("no such compose file"));

    // ── Override rendering ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Render_WritesOnlyThePlannedServicesInPlanOrder() {
        var plan = EnvInjectionPlan.Create(new EnvInjectionRequest(
            [new EnvInjectionService("worker"), new EnvInjectionService("db"),
             new EnvInjectionService("web", "true")],
            StackId: 7,
            AppApiToken: "wtapp_abc",
            PublicBaseUrl: "https://watchtower.example.com"));

        Assert.Equal(
            """
            # Generated by Watchtower for this deploy — not part of the repository.
            services:
              'db':
                environment:
                  'WATCHTOWER_STACK_ID': '7'
                  'WATCHTOWER_URL': 'https://watchtower.example.com'
              'web':
                environment:
                  'WATCHTOWER_APP_TOKEN': 'wtapp_abc'
                  'WATCHTOWER_STACK_ID': '7'
                  'WATCHTOWER_URL': 'https://watchtower.example.com'
              'worker':
                environment:
                  'WATCHTOWER_STACK_ID': '7'
                  'WATCHTOWER_URL': 'https://watchtower.example.com'

            """.ReplaceLineEndings("\n"),
            ComposeOverrideFile.Render(plan));
    }

    /// <summary>A <c>services:</c> key with nothing under it is not a Compose file, so nothing is written.</summary>
    [Fact]
    public void Render_ReturnsNullWhenThePlanInjectsNothing() =>
        Assert.Null(ComposeOverrideFile.Render(EnvInjectionPlan.Empty));

    /// <summary>
    /// Compose interpolates <c>$</c> in every file it reads, this generated one included. A token
    /// containing one would otherwise be substituted away — silently, and into an empty string.
    /// </summary>
    [Fact]
    public void Render_EscapesDollarSignsAgainstComposeInterpolation() {
        Assert.Contains(
            "'WATCHTOWER_APP_TOKEN': 'wtapp_$${SECRET}a$$b'",
            RenderSingleService("wtapp_${SECRET}a$b"));
    }

    [Fact]
    public void Render_EscapesSingleQuotesByDoublingThem() {
        Assert.Contains("'WATCHTOWER_APP_TOKEN': 'it''s a token'", RenderSingleService("it's a token"));
    }

    [Theory]
    [InlineData("a value with spaces")]
    [InlineData("  leading and trailing  ")]
    [InlineData("#not-a-comment")]
    [InlineData("true")]
    [InlineData("{}[]:,&*!|>%@`\"")]
    public void Render_QuotesValuesThatWouldOtherwiseChangeMeaning(string token) {
        Assert.Contains($"'WATCHTOWER_APP_TOKEN': '{token}'", RenderSingleService(token));
    }

    /// <summary>
    /// No injected value is expected to contain a line break — but a single-quoted YAML scalar folds
    /// one into a space rather than failing, so the fallback that keeps it intact is pinned here.
    /// </summary>
    [Fact]
    public void Render_FallsBackToADoubleQuotedScalarForALineBreak() {
        Assert.Contains(@"'WATCHTOWER_APP_TOKEN': ""one\ntwo""", RenderSingleService("one\ntwo"));
    }

    [Fact]
    public void Render_QuotesServiceNames() {
        var plan = EnvInjectionPlan.Create(new EnvInjectionRequest(
            [new EnvInjectionService("no")], StackId: 1, AppApiToken: "wtapp_x"));

        Assert.Contains("  'no':\n", ComposeOverrideFile.Render(plan));
    }

    private static string RenderSingleService(string token) {
        var plan = EnvInjectionPlan.Create(new EnvInjectionRequest(
            [new EnvInjectionService("app")], StackId: 1, AppApiToken: token));
        return ComposeOverrideFile.Render(plan)!;
    }
}
