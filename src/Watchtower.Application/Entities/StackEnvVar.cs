namespace Watchtower.Application.Entities;

/// <summary>
/// An environment variable override for a stack. Injected into every deploy via an
/// <c>--env-file</c> passed to docker compose, so it is available for <c>${VAR}</c>
/// substitution in the compose YAML.
/// </summary>
public sealed class StackEnvVar {
    public int Id { get; set; }
    public int StackId { get; set; }
    public Stack? Stack { get; set; }
    public required string Key { get; set; }
    public required string Value { get; set; }
}
