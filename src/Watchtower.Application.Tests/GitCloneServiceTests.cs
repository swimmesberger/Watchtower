using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers the parts of <see cref="GitCloneService"/> that decide something before a subprocess exists:
/// the commit-SHA guard on <see cref="GitCloneService.CloneAtCommitAsync"/>, and that the method is a
/// seam a test double can stand in for.
/// </summary>
/// <remarks>
/// What git does with the arguments is git's business and needs a remote; what is worth pinning here is
/// that a request which cannot be a commit never reaches it. The SHA is interpolated into an argument
/// list and then checked out, and "not a commit" is a clearer failure at the door than whatever git
/// would make of it three commands later.
/// </remarks>
public sealed class GitCloneServiceTests {
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData("")]
    [InlineData("abc1234")]                                       // the short form is not enough
    [InlineData("0123456789abcdef0123456789abcdef0123456")]       // 39 characters
    [InlineData("0123456789abcdef0123456789abcdef012345678")]     // 41 characters
    [InlineData("0123456789abcdef0123456789abcdef0123456g")]      // not hexadecimal
    [InlineData("main")]
    [InlineData("--upload-pack=touch /tmp/pwned")]
    public async Task CloneAtCommitAsync_RefusesAnythingThatIsNotACommitSha(string commitSha) {
        var targetDir = Path.Combine(Path.GetTempPath(), $"watchtower-test-{Guid.NewGuid():N}");
        var lines = new List<string>();

        var (exitCode, output) = await new GitCloneService().CloneAtCommitAsync(
            "https://example.invalid/repo.git", "main", commitSha, token: null, targetDir,
            lines.Add, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("is not a 40-character commit SHA", output, StringComparison.Ordinal);
        // Reported through the live stream too, not only in the captured output.
        Assert.Contains(lines, l => l.Contains("is not a 40-character commit SHA", StringComparison.Ordinal));
        // Nothing was run, so nothing was created either.
        Assert.False(Directory.Exists(targetDir));
    }

    /// <summary>
    /// Virtual for the same reason <see cref="GitCloneService.CloneAsync"/> is: a release-pinned deploy
    /// clones at a commit, and no test has a remote holding one.
    /// </summary>
    [Fact]
    public async Task CloneAtCommitAsync_IsASeamADoubleCanStandInFor() {
        var git = new StubGitCloneService();
        var targetDir = Path.Combine(Path.GetTempPath(), $"watchtower-test-{Guid.NewGuid():N}");

        try {
            GitCloneService seam = git;
            var (exitCode, _) = await seam.CloneAtCommitAsync(
                "https://example.invalid/repo.git", "main", Sha, token: null, targetDir,
                onLine: null, TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.Equal(Sha, git.RequestedCommit);
            // A checkout the rest of the deploy can work in, reporting the commit it was asked for.
            Assert.True(Directory.Exists(targetDir));
            Assert.Equal(Sha, await seam.GetHeadCommitAsync(targetDir, TestContext.Current.CancellationToken));
        } finally {
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, recursive: true);
        }
    }
}
