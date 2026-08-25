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

    // ── Merging the image-pin plan into the same override ────────────────────────────────────────

    /// <summary>
    /// One file, both policies: <c>image:</c> before <c>environment:</c> per service, services in
    /// ordinal name order, and a service that only one plan names still gets its own entry.
    /// </summary>
    [Fact]
    public void Render_MergesImagePinsAndEnvironmentIntoOneDocument() {
        var plan = EnvInjectionPlan.Create(new EnvInjectionRequest(
            [new EnvInjectionService("web", "true"), new EnvInjectionService("db")],
            StackId: 7,
            AppApiToken: "wtapp_abc"));
        // 'jobs' is in the pin plan only — a service the env policy gave nothing to still needs its
        // image rewritten, so the merge is a union rather than a lookup on one side.
        var imagePlan = new ImagePinPlan(
            [new ServiceImagePin("jobs", "ghcr.io/acme/jobs@sha256:cc"),
             new ServiceImagePin("web", "ghcr.io/acme/web@sha256:ab")],
            []);

        Assert.Equal(
            """
            # Generated by Watchtower for this deploy — not part of the repository.
            services:
              'db':
                environment:
                  'WATCHTOWER_STACK_ID': '7'
              'jobs':
                image: 'ghcr.io/acme/jobs@sha256:cc'
              'web':
                image: 'ghcr.io/acme/web@sha256:ab'
                environment:
                  'WATCHTOWER_APP_TOKEN': 'wtapp_abc'
                  'WATCHTOWER_STACK_ID': '7'

            """.ReplaceLineEndings("\n"),
            ComposeOverrideFile.Render(plan, imagePlan));
    }

    /// <summary>
    /// A pin alone is enough to write a file: a release-mode deploy of a project the env policy has
    /// nothing to say about still has to rewrite its images.
    /// </summary>
    [Fact]
    public void Render_WritesAFileForImagePinsAlone() {
        var imagePlan = new ImagePinPlan([new ServiceImagePin("api", "ghcr.io/acme/api@sha256:ab")], []);

        Assert.Equal(
            """
            # Generated by Watchtower for this deploy — not part of the repository.
            services:
              'api':
                image: 'ghcr.io/acme/api@sha256:ab'

            """.ReplaceLineEndings("\n"),
            ComposeOverrideFile.Render(EnvInjectionPlan.Empty, imagePlan));
    }

    /// <summary>
    /// The back-compat guarantee at this seam: with no pins to merge, the renderer emits the exact
    /// document it emitted before image pinning existed — services in ordinal name order, no
    /// <c>image:</c> line anywhere, and nothing at all for an empty plan.
    /// </summary>
    /// <remarks>
    /// Asserted against a literal rather than against <c>Render(plan)</c>, which would only restate that
    /// the parameter defaults to null. The ordering claim is the part worth pinning: the merge walks the
    /// union of both plans' service names through its own <c>OrderBy</c>, where the single-plan version
    /// walked <see cref="EnvInjectionPlan.Services"/> directly. Those agree only because
    /// <see cref="EnvInjectionPlan.Create"/> already orders ordinally by name — note the input below is
    /// deliberately given out of order — and if it ever stopped, this is what would notice.
    /// </remarks>
    [Fact]
    public void Render_IsUnchangedWithoutAnImagePlan() {
        var plan = EnvInjectionPlan.Create(new EnvInjectionRequest(
            [new EnvInjectionService("worker"), new EnvInjectionService("web", "true")],
            StackId: 7, AppApiToken: "wtapp_abc"));

        const string expected = """
            # Generated by Watchtower for this deploy — not part of the repository.
            services:
              'web':
                environment:
                  'WATCHTOWER_APP_TOKEN': 'wtapp_abc'
                  'WATCHTOWER_STACK_ID': '7'
              'worker':
                environment:
                  'WATCHTOWER_STACK_ID': '7'

            """;
        Assert.Equal(expected.ReplaceLineEndings("\n"), ComposeOverrideFile.Render(plan, imagePlan: null));
        // An image plan that pins nothing is the same document, not a different one.
        Assert.Equal(expected.ReplaceLineEndings("\n"), ComposeOverrideFile.Render(plan, ImagePinPlan.Empty));
        Assert.Null(ComposeOverrideFile.Render(EnvInjectionPlan.Empty, ImagePinPlan.Empty));
    }

    /// <summary>
    /// A pinned image goes through the same escaping as every injected value. Digests do not contain a
    /// <c>$</c> today; a value-specific exemption is exactly how that would stop being true unnoticed.
    /// </summary>
    [Fact]
    public void Render_EscapesAPinnedImageAgainstComposeInterpolation() {
        var imagePlan = new ImagePinPlan([new ServiceImagePin("api", "ghcr.io/acme/api:${TAG}")], []);

        Assert.Contains(
            "image: 'ghcr.io/acme/api:$${TAG}'",
            ComposeOverrideFile.Render(EnvInjectionPlan.Empty, imagePlan)!,
            StringComparison.Ordinal);
    }

    private static string RenderSingleService(string token) {
        var plan = EnvInjectionPlan.Create(new EnvInjectionRequest(
            [new EnvInjectionService("app")], StackId: 1, AppApiToken: token));
        return ComposeOverrideFile.Render(plan)!;
    }
}
