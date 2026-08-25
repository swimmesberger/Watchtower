using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The dedupe rule that decides whether two git sources are one product (ADR-0026). It runs in two
/// places — here, behind <c>stacks.create</c>'s find-or-create, and in SQL, in the backfill migration —
/// so what it treats as equal is a contract, not an implementation detail.
/// </summary>
public sealed class ProductSourceKeyTests {
    [Theory]
    // Cosmetic differences that must not fork a product.
    [InlineData("https://github.com/acme/web", "https://github.com/acme/web.git")]
    [InlineData("https://github.com/acme/web", "https://github.com/acme/web/")]
    [InlineData("https://github.com/acme/web", "https://github.com/acme/web.git/")]
    [InlineData("https://github.com/acme/web", "  https://github.com/acme/web  ")]
    [InlineData("https://github.com/acme/web", "https://GitHub.com/acme/web")]
    [InlineData("https://github.com/acme/web", "HTTPS://github.com/acme/web")]
    [InlineData("https://github.com/acme/web", "https://github.com/acme/web.GIT")]
    public void FoldsCosmeticDifferencesInTheUrl(string left, string right) =>
        Assert.Equal(
            ProductSourceKey.NormalizeRepositoryUrl(left),
            ProductSourceKey.NormalizeRepositoryUrl(right));

    [Theory]
    // The path is not case-folded: plenty of git servers are case-sensitive there, and merging
    // two repositories that differ only in case would be the worse mistake.
    [InlineData("https://github.com/acme/web", "https://github.com/Acme/Web")]
    // Different compose file in the same repository → deliberately a different product.
    [InlineData("https://github.com/acme/web", "https://github.com/acme/web-2")]
    // No scheme unification: see the type's own documentation.
    [InlineData("https://github.com/acme/web", "git@github.com:acme/web.git")]
    [InlineData("https://github.com/acme/web", "ssh://git@github.com/acme/web")]
    public void KeepsMeaningfulDifferencesApart(string left, string right) =>
        Assert.NotEqual(
            ProductSourceKey.NormalizeRepositoryUrl(left),
            ProductSourceKey.NormalizeRepositoryUrl(right));

    /// <summary>
    /// The scp-like form gets the same trimming and host-folding as any other, just as its own key.
    /// </summary>
    [Fact]
    public void FoldsTheHostOfAnScpStyleUrlToo() {
        Assert.Equal("git@github.com:acme/web", ProductSourceKey.NormalizeRepositoryUrl("git@GitHub.com:acme/web.git"));
        Assert.Equal(
            ProductSourceKey.NormalizeRepositoryUrl("git@github.com:acme/web"),
            ProductSourceKey.NormalizeRepositoryUrl("git@GITHUB.COM:acme/web.git/"));
    }

    /// <summary>Userinfo is left alone: an access token is not a hostname, and case matters in it.</summary>
    [Fact]
    public void LeavesUserinfoUntouchedWhileFoldingTheHost() =>
        Assert.Equal(
            "https://AbC@git.example.com/acme/web",
            ProductSourceKey.NormalizeRepositoryUrl("https://AbC@Git.Example.COM/acme/web.git"));

    [Theory]
    [InlineData("docker-compose.yml", "docker-compose.yml")]
    [InlineData("/docker-compose.yml", "docker-compose.yml")]
    [InlineData("\\docker-compose.yml", "docker-compose.yml")]
    [InlineData("  apps/web/compose.yaml  ", "apps/web/compose.yaml")]
    [InlineData(null, "")]
    public void NormalizesTheComposePathLikeTheDeployDoes(string? input, string expected) =>
        Assert.Equal(expected, ProductSourceKey.NormalizeComposeFilePath(input));

    [Theory]
    [InlineData("https://github.com/acme/web.git", "web")]
    [InlineData("https://github.com/acme/web/", "web")]
    [InlineData("git@github.com:acme/web.git", "web")]
    // No path at all: the host is the only thing left, and it beats "unnamed".
    [InlineData("https://git.example.com/", "git.example.com")]
    // Nothing usable at all.
    [InlineData("https://", ProductSourceKey.FallbackName)]
    [InlineData("", ProductSourceKey.FallbackName)]
    public void DerivesTheProductNameFromTheRepositoryPath(string url, string expected) =>
        Assert.Equal(expected, ProductSourceKey.DeriveName(url));

    /// <summary>
    /// The pair is what products are keyed on: the same repository with two compose files is two
    /// products, and the same compose file reached by two spellings of one URL is one.
    /// </summary>
    [Fact]
    public void KeysOnTheUrlAndTheComposePathTogether() {
        Assert.Equal(
            ProductSourceKey.Create("https://github.com/acme/web.git", "/docker-compose.yml"),
            ProductSourceKey.Create("https://GitHub.com/acme/web/", "docker-compose.yml"));
        Assert.NotEqual(
            ProductSourceKey.Create("https://github.com/acme/web", "apps/api/compose.yaml"),
            ProductSourceKey.Create("https://github.com/acme/web", "apps/web/compose.yaml"));
    }
}
