using System.Text.RegularExpressions;

namespace Watchtower.Application.Services;

/// <summary>
/// The dedupe key that decides whether two git sources are the same product (ADR-0026): the
/// repository URL and the compose file path, each normalized so that cosmetic differences — a
/// trailing slash, a <c>.git</c> suffix, a capitalized hostname, a leading slash on the compose
/// path — do not fork one repository into several products.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the C# half of a rule that is also written in SQL, in
/// <see cref="Persistence.ProductBackfillSql"/>.</b> The same normalization runs twice: once here, for
/// <c>stacks.create</c>'s find-or-create at runtime, and once in PostgreSQL, for the migration that
/// gave every pre-ADR-0026 row its product. If the two disagree, an upgraded installation forks a
/// duplicate product the moment someone creates a stack. Each file's header points at the other.
/// </para>
/// <para>
/// <b>Schemes are not unified.</b> <c>git@github.com:acme/web.git</c> and
/// <c>https://github.com/acme/web</c> normalize to <em>different</em> keys, so they become two
/// products. That is deliberate: deciding the two forms are the same repository means asserting that
/// one credential clones both, and an SSH key and an HTTPS token are exactly the case where that is
/// false. Merging them would silently hand one stack the other's credential; keeping them apart costs
/// an operator one merge they can perform themselves.
/// </para>
/// </remarks>
public static partial class ProductSourceKey {
    /// <summary>Fallback product name when a repository URL yields no usable last path segment.</summary>
    public const string FallbackName = "unnamed";

    /// <summary><c>scheme://[userinfo@]host[:port]</c> — group 1 scheme, 2 userinfo, 3 host, 4 rest.</summary>
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9+.\-]*://)([^/@]*@)?([^/]*)(.*)$")]
    private static partial Regex SchemeUrl();

    /// <summary>scp-like <c>[user@]host:path</c> — group 1 userinfo, 2 host, 3 the rest from the colon.</summary>
    [GeneratedRegex(@"^([^/@:]*@)?([^/:]+)(:.*)$")]
    private static partial Regex ScpUrl();

    /// <summary>Everything up to and including the last <c>/</c> or <c>:</c>.</summary>
    [GeneratedRegex(@"^.*[/:]")]
    private static partial Regex UpToLastSegment();

    /// <summary>The normalized <c>(repository URL, compose path)</c> pair two sources are compared on.</summary>
    public static (string RepositoryUrl, string ComposeFilePath) Create(string? repositoryUrl, string? composeFilePath) =>
        (NormalizeRepositoryUrl(repositoryUrl), NormalizeComposeFilePath(composeFilePath));

    /// <summary>
    /// Trims, drops trailing slashes and a trailing <c>.git</c>, and lowercases the scheme and host
    /// (never the path — plenty of git servers are case-sensitive there, and plenty of hosts are not).
    /// </summary>
    public static string NormalizeRepositoryUrl(string? repositoryUrl) {
        var url = (repositoryUrl ?? string.Empty).Trim().TrimEnd('/');
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
        url = url.TrimEnd('/');

        if (SchemeUrl().Match(url) is { Success: true } scheme) {
            return scheme.Groups[1].Value.ToLowerInvariant()
                + scheme.Groups[2].Value
                + scheme.Groups[3].Value.ToLowerInvariant()
                + scheme.Groups[4].Value;
        }
        if (ScpUrl().Match(url) is { Success: true } scp) {
            return scp.Groups[1].Value
                + scp.Groups[2].Value.ToLowerInvariant()
                + scp.Groups[3].Value;
        }
        // A bare path, a Windows path, anything else: nothing here is a host, so nothing is folded.
        return url;
    }

    /// <summary>
    /// Trims and drops leading path separators, so <c>/docker-compose.yml</c> and
    /// <c>docker-compose.yml</c> are one product — the same <c>TrimStart('/', '\\')</c> the deploy
    /// applies before joining the path onto the clone directory.
    /// </summary>
    public static string NormalizeComposeFilePath(string? composeFilePath) =>
        (composeFilePath ?? string.Empty).Trim().TrimStart('/', '\\');

    /// <summary>
    /// The name a find-or-created product gets: the repository URL's last path segment, or
    /// <see cref="FallbackName"/> when there is nothing usable — the same fallback
    /// <see cref="BackupNaming.Sanitize"/> uses for a name that sanitizes away to nothing.
    /// </summary>
    public static string DeriveName(string? repositoryUrl) {
        var segment = UpToLastSegment().Replace(NormalizeRepositoryUrl(repositoryUrl), string.Empty).Trim();
        return segment.Length == 0 ? FallbackName : segment;
    }
}
