using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The URL parser is the gate of the stack↔CI link: whatever it returns becomes the
/// <c>owner/name</c> key of a shared <c>CiRepo</c>, so it must accept every way a GitHub remote is
/// commonly written and reject everything that is not a github.com repository.
/// </summary>
public sealed class GitHubRepoUrlTests {
    [Theory]
    [InlineData("https://github.com/swimmesberger/Watchtower", "swimmesberger", "Watchtower")]
    [InlineData("https://github.com/swimmesberger/Watchtower.git", "swimmesberger", "Watchtower")]
    [InlineData("https://github.com/swimmesberger/Watchtower/", "swimmesberger", "Watchtower")]
    [InlineData("http://github.com/owner/repo.git", "owner", "repo")]
    [InlineData("git@github.com:owner/repo.git", "owner", "repo")]
    [InlineData("git@github.com:owner/repo", "owner", "repo")]
    [InlineData("ssh://git@github.com/owner/repo.git", "owner", "repo")]
    [InlineData("  https://github.com/owner/repo.git  ", "owner", "repo")]
    [InlineData("https://GITHUB.COM/Owner/Repo.GIT", "Owner", "Repo")]
    public void TryParse_AcceptsGitHubRemoteForms(string url, string owner, string name) {
        Assert.Equal((owner, name), GitHubRepoUrl.TryParse(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://gitlab.com/owner/repo.git")]
    [InlineData("https://example.invalid/shop.git")]
    [InlineData("git@gitea.local:owner/repo.git")]
    [InlineData("https://github.com/owner")]
    [InlineData("https://github.com/owner/repo/extra")]
    [InlineData("https://github.com/")]
    [InlineData("not a url at all")]
    public void TryParse_RejectsEverythingElse(string? url) {
        Assert.Null(GitHubRepoUrl.TryParse(url));
    }
}
