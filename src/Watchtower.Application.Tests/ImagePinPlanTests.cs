using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="ImagePinPlan"/>: which compose services a release rewrites the image of, and what
/// it says about the ones it does not (docs/products/design.md, "Image pinning").
/// </summary>
/// <remarks>
/// Pure policy, so every case here is a table: no daemon, no registry, no database. The rules that
/// matter are the ones that decide <em>not</em> to pin — a false positive rewrites somebody's
/// <c>postgres:16</c> to an application image, and a silent false negative deploys an unpinned stack
/// that the UI claims is on a release.
/// </remarks>
public sealed class ImagePinPlanTests {
    private const string ApiDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string WebDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    private static readonly ReleaseImageRef[] Release = [
        new("ghcr.io/acme/api", ApiDigest),
        new("ghcr.io/acme/web", WebDigest),
    ];

    /// <summary>
    /// The match rule: repository identity, tag and digest ignored. A service already running the
    /// release's repository under a moving tag is exactly the case pinning exists for.
    /// </summary>
    [Fact]
    public void Create_PinsTheServicesWhoseRepositoryTheReleaseNames() {
        var plan = ImagePinPlan.Create([
            new EnvInjectionService("api", Image: "ghcr.io/acme/api:latest"),
            new EnvInjectionService("web", Image: "ghcr.io/acme/web:2026.8"),
        ], Release);

        Assert.Equal(
            [new ServiceImagePin("api", $"ghcr.io/acme/api@{ApiDigest}"),
             new ServiceImagePin("web", $"ghcr.io/acme/web@{WebDigest}")],
            plan.Services);
        Assert.Empty(plan.Warnings);
    }

    /// <summary>
    /// No allowlist is needed, and none exists: a sidecar the release did not build simply matches
    /// nothing. <c>postgres:16</c> normalizes to <c>docker.io/library/postgres</c>, which is the whole
    /// reason the comparison is on the canonical repository.
    /// </summary>
    [Fact]
    public void Create_LeavesAServiceTheReleaseHasNoImageForAlone() {
        var plan = ImagePinPlan.Create([
            new EnvInjectionService("db", Image: "postgres:16"),
            new EnvInjectionService("cache", Image: "redis:7"),
        ], Release);

        Assert.Empty(plan.Services);
        // Silently — a stack's sidecars are not a warning, they are the normal case.
        Assert.Empty(plan.Warnings);
    }

    /// <summary>
    /// A registry the release did not publish to is a different repository, even for the same path.
    /// This is the mirror-host caveat: the label is the escape hatch, not a fuzzy match.
    /// </summary>
    [Fact]
    public void Create_DoesNotMatchTheSamePathOnADifferentRegistry() {
        var plan = ImagePinPlan.Create(
            [new EnvInjectionService("api", Image: "mirror.example.com/acme/api:latest")], Release);

        Assert.Empty(plan.Services);
    }

    /// <summary>
    /// <c>"false"</c> outranks the match: a service deliberately running a published tag stays on it,
    /// which is the only way to say so.
    /// </summary>
    [Fact]
    public void Create_ExemptsALabelledServiceEvenWhenItMatches() {
        var plan = ImagePinPlan.Create([
            new EnvInjectionService("api", Image: "ghcr.io/acme/api:latest", ReleaseImageLabel: "false"),
            new EnvInjectionService("web", Image: "ghcr.io/acme/web:latest"),
        ], Release);

        Assert.Equal([new ServiceImagePin("web", $"ghcr.io/acme/web@{WebDigest}")], plan.Services);
        Assert.Empty(plan.Warnings);
    }

    /// <summary>
    /// <c>"true"</c> with nothing to pin warns and continues. Failing instead would take a whole fleet
    /// down the first time somebody added a service ahead of the build that produces its image.
    /// </summary>
    [Fact]
    public void Create_WarnsButContinuesWhenAForcedServiceHasNoMatchingReleaseImage() {
        var plan = ImagePinPlan.Create([
            new EnvInjectionService("jobs", Image: "ghcr.io/acme/jobs:latest", ReleaseImageLabel: "true"),
            new EnvInjectionService("api", Image: "ghcr.io/acme/api:latest"),
        ], Release);

        // The rest of the plan is unaffected — that is what "continues" means.
        Assert.Equal([new ServiceImagePin("api", $"ghcr.io/acme/api@{ApiDigest}")], plan.Services);
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("'jobs'", warning, StringComparison.Ordinal);
        Assert.Contains("ghcr.io/acme/jobs", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unusable label value is reported and read as absent — so the repository match still applies.
    /// Guessing either way is wrong invisibly: opt-in rewrites an image the author excluded, opt-out
    /// deploys an unpinned service that claims to be on a release.
    /// </summary>
    [Fact]
    public void Create_ReportsAnUnparseableLabelAndFallsBackToTheRepositoryMatch() {
        var plan = ImagePinPlan.Create(
            [new EnvInjectionService("api", Image: "ghcr.io/acme/api:latest", ReleaseImageLabel: "yes")],
            Release);

        Assert.Equal([new ServiceImagePin("api", $"ghcr.io/acme/api@{ApiDigest}")], plan.Services);
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("unrecognized watchtower.release-image value 'yes'", warning, StringComparison.Ordinal);
    }

    /// <summary>The label's tolerance matches the inject-token label's: casing and surrounding space.</summary>
    [Theory]
    [InlineData("False")]
    [InlineData(" false ")]
    [InlineData("FALSE")]
    public void Create_ReadsTheLabelAsTolerantlyAsTheInjectTokenLabel(string label) {
        var plan = ImagePinPlan.Create(
            [new EnvInjectionService("api", Image: "ghcr.io/acme/api:latest", ReleaseImageLabel: label)],
            Release);

        Assert.Empty(plan.Services);
        Assert.Empty(plan.Warnings);
    }

    /// <summary>A build-only service declares no image; there is nothing to rewrite and nothing to say.</summary>
    [Fact]
    public void Create_SkipsABuildOnlyServiceSilently() {
        var plan = ImagePinPlan.Create([new EnvInjectionService("api")], Release);

        Assert.Empty(plan.Services);
        Assert.Empty(plan.Warnings);
    }

    /// <summary>…unless its author asked for a pin it cannot have, which is worth one line.</summary>
    [Fact]
    public void Create_WarnsWhenAForcedServiceBuildsItsImage() {
        var plan = ImagePinPlan.Create(
            [new EnvInjectionService("api", ReleaseImageLabel: "true")], Release);

        Assert.Empty(plan.Services);
        Assert.Contains("builds its image", Assert.Single(plan.Warnings), StringComparison.Ordinal);
    }

    /// <summary>
    /// An image the engine reported but that does not parse is named rather than skipped: it is the one
    /// case where pinning would otherwise stop working with nothing to show for it.
    /// </summary>
    [Fact]
    public void Create_WarnsAboutAnImageItCannotRead() {
        var plan = ImagePinPlan.Create([new EnvInjectionService("api", Image: "  ")], Release);

        // Blank is "no image" — a build-only service, silently skipped.
        Assert.Empty(plan.Warnings);

        var broken = ImagePinPlan.Create([new EnvInjectionService("api", Image: "ghcr.io/acme/api@nonsense")], Release);
        Assert.Empty(broken.Services);
        Assert.Contains("could not read the image reference", Assert.Single(broken.Warnings), StringComparison.Ordinal);
    }

    /// <summary>Deterministic ordering, so a rendered override is diffable between deploys.</summary>
    [Fact]
    public void Create_OrdersServicesByName() {
        var plan = ImagePinPlan.Create([
            new EnvInjectionService("web", Image: "ghcr.io/acme/web:1"),
            new EnvInjectionService("api", Image: "ghcr.io/acme/api:1"),
        ], Release);

        Assert.Equal(["api", "web"], plan.Services.Select(s => s.ServiceName));
    }

    /// <summary>A release with no images pins nothing — and neither does an empty project.</summary>
    [Fact]
    public void Create_IsEmptyWithoutServicesOrReleaseImages() {
        Assert.Same(ImagePinPlan.Empty, ImagePinPlan.Create([], Release));
        Assert.Same(
            ImagePinPlan.Empty,
            ImagePinPlan.Create([new EnvInjectionService("api", Image: "ghcr.io/acme/api:1")], []));
    }
}
