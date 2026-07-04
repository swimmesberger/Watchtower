namespace Watchtower.Application.Entities;

/// <summary>
/// A general-purpose stored credential (username + token) used for git repository cloning
/// and Docker registry authentication.
/// </summary>
/// <remarks>
/// For GitHub Container Registry (ghcr.io) use a <em>classic</em> PAT — fine-grained PATs
/// cannot authenticate to ghcr.io.
/// </remarks>
public sealed class Credential {
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Username { get; set; }
    public required string Token { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
