namespace Watchtower.Application.Entities;

/// <summary>A single key/value application setting (e.g. self-update overrides and cached check results).</summary>
public sealed class AppSetting {
    public required string Key { get; set; }
    public required string Value { get; set; }
}
