namespace Watchtower.Application.Services;

/// <summary>
/// Parses GitHub repository URLs into <c>owner/name</c>. Pure so the stack↔CI link (which keys
/// <see cref="Entities.CiRepo"/> on the parsed pair) is unit-testable, and deliberately strict about
/// the host: only github.com repositories can get Actions runners, so anything else returns null
/// rather than a guess.
/// </summary>
public static class GitHubRepoUrl {
    /// <summary>
    /// Extracts <c>(Owner, Name)</c> from an HTTPS (<c>https://github.com/owner/repo[.git]</c>),
    /// SCP-style SSH (<c>git@github.com:owner/repo.git</c>) or SSH-URL
    /// (<c>ssh://git@github.com/owner/repo.git</c>) form. Returns null for non-GitHub remotes,
    /// deeper paths, or anything that does not look like a repository URL.
    /// </summary>
    public static (string Owner, string Name)? TryParse(string? repositoryUrl) {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return null;
        var url = repositoryUrl.Trim();

        string? path = null;
        if (url.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) {
            path = url["git@github.com:".Length..];
        } else if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
                   && uri.Scheme is "https" or "http" or "ssh" or "git"
                   && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) {
            path = uri.AbsolutePath.TrimStart('/');
        }
        if (path is null)
            return null;

        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            path = path[..^4];
        path = path.TrimEnd('/');

        var segments = path.Split('/');
        if (segments.Length != 2)
            return null;
        var owner = segments[0];
        var name = segments[1];
        if (owner.Length == 0 || name.Length == 0)
            return null;
        return (owner, name);
    }
}
