using Microsoft.Extensions.Configuration;

namespace Watchtower.Application.Tests;

/// <summary>
/// An in-memory configuration source whose values can be replaced, standing in for the Elarion settings
/// store — the reloadable layer a settings write reaches configuration through (ADR-0014). Everything
/// about the ingress listeners following the reverse-proxy settings depends on that reload actually
/// propagating, so the tests drive it directly rather than through a database.
/// </summary>
internal sealed class ReloadableSettings : IConfigurationSource {
    private readonly ReloadableProvider _provider;

    public ReloadableSettings(params (string Key, string? Value)[] initial) =>
        _provider = new ReloadableProvider(Map(initial));

    public IConfigurationProvider Build(IConfigurationBuilder builder) => _provider;

    /// <summary>Replaces every value and raises the reload token, as a settings write does.</summary>
    public void Publish(params (string Key, string? Value)[] settings) => _provider.Publish(Map(settings));

    private static Dictionary<string, string?> Map((string Key, string? Value)[] settings) {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in settings) data[key] = value;
        return data;
    }

    private sealed class ReloadableProvider(Dictionary<string, string?> initial) : ConfigurationProvider {
        private readonly Dictionary<string, string?> _initial = initial;

        public override void Load() => Data = _initial;

        public void Publish(Dictionary<string, string?> data) {
            Data = data;
            OnReload();
        }
    }
}
