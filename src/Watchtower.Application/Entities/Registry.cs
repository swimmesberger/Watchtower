namespace Watchtower.Application.Entities;

/// <summary>A Docker registry associated with an optional stored <see cref="Credential"/>.</summary>
public sealed class Registry {
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    /// <summary>Optional link to a credential. Set to null when the credential is deleted.</summary>
    public int? CredentialId { get; set; }
    public Credential? Credential { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
