namespace Watchtower.Application.Config;

/// <summary>
/// The auth mode this process actually started with. <c>Auth:Enabled</c> is read before DI is built
/// (it decides middleware registration, endpoint mapping, and the <c>ICurrentUser</c> implementation —
/// see <c>Program.cs</c>), so a runtime change to the stored setting only takes effect after a restart.
/// The auth settings handlers compare the configured value against this snapshot to report
/// "restart required" instead of pretending the toggle applied.
/// </summary>
/// <param name="Enabled">Whether the auth pipeline is active in this process.</param>
public sealed record AuthStartupState(bool Enabled);
