using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// The vocabulary shared by the two ends of a port route's listener (ADR-0033): the setting value that
/// carries the ports, and the Kestrel endpoint name each of them binds under.
/// </summary>
/// <remarks>
/// Pure, and deliberately forgiving. <see cref="Parse"/> is read by
/// <see cref="ProxyIngressKestrelConfiguration"/> before the host exists — the same place where an
/// unparseable port setting is a listener that stays off rather than a stack trace at startup — so a value
/// it cannot make sense of costs the entries it could not read and nothing else. What it can never do is
/// throw.
/// </remarks>
public static class PortRouteListeners {
    /// <summary>
    /// The prefix every port route's Kestrel endpoint is named with. Distinct from <c>ProxyHttp</c> and
    /// <c>ProxyHttps</c> in both directions: neither is a prefix of the other, so the masking rules and
    /// <see cref="IsPortEndpointName"/> can tell the three kinds apart on the name alone.
    /// </summary>
    private const string EndpointNamePrefix = "ProxyPort";

    /// <summary>The Kestrel endpoint name a port route's listener binds under.</summary>
    public static string EndpointName(int port) =>
        string.Create(CultureInfo.InvariantCulture, $"{EndpointNamePrefix}{port}");

    /// <summary>Whether a Kestrel endpoint name is one of the port routes'.</summary>
    public static bool IsPortEndpointName(string? name) => TryParseEndpointName(name, out _);

    /// <summary>
    /// The port an endpoint name carries, for reading a projected section back. Only a name whose suffix
    /// is a plausible port matches — <c>ProxyPortimatelyFine</c> is somebody else's endpoint.
    /// </summary>
    public static bool TryParseEndpointName(string? name, out int port) {
        port = 0;
        if (name is null || !name.StartsWith(EndpointNamePrefix, StringComparison.OrdinalIgnoreCase))
            return false;
        return TryParsePort(name.AsSpan(EndpointNamePrefix.Length), out port);
    }

    /// <summary>
    /// The ports the projected Kestrel section gives a port route a listener on — read back out of the
    /// section rather than out of the setting it was derived from, so a port the projection <em>dropped</em>
    /// (one colliding with the management or ingress ports) is absent here too.
    /// </summary>
    /// <remarks>
    /// The single definition of that set, because two of them would eventually disagree and both consumers
    /// act on it at a moment where disagreeing is a fault: the TLS hook decides whether a listener being
    /// created is a port route's, and the listener state publishes what the dispatcher then routes by.
    /// Deliberately <em>not</em> read from <see cref="YarpListenerState"/> by the TLS hook — the state is
    /// republished from a reload callback of this same section, and the order in which the two callbacks
    /// run is not something either of them gets to assume.
    /// </remarks>
    /// <param name="kestrelSection">The projected section; keys relative to <c>Kestrel</c>.</param>
    public static IReadOnlySet<int> BoundPorts(IConfiguration kestrelSection) {
        ArgumentNullException.ThrowIfNull(kestrelSection);
        var ports = new HashSet<int>();
        foreach (var endpoint in kestrelSection.GetSection("Endpoints").GetChildren()) {
            if (!IsPortEndpointName(endpoint.Key)) continue;
            // The URL rather than the name: what Kestrel binds is what this has to describe, even though
            // the projection writes the two from one number.
            if (ListenerUrl.PortOf(endpoint["Url"]) is { } port) ports.Add(port);
        }
        return ports;
    }

    /// <summary>
    /// The ports as one setting value: ascending, deduplicated, comma-separated. Canonical, because
    /// <see cref="YarpProxyProvider.ApplyAsync"/> compares the rendering against the stored one to decide
    /// whether to write at all, and two spellings of the same set would make every pass a write.
    /// </summary>
    public static string Format(IEnumerable<int> ports) {
        ArgumentNullException.ThrowIfNull(ports);
        return string.Join(',', Normalize(ports).Select(p => p.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// The ports a setting value names, in the same canonical order <see cref="Format"/> produces. An
    /// entry that is not a port in range is dropped rather than refused: the alternative is a single bad
    /// character costing every other route its listener.
    /// </summary>
    public static IReadOnlyList<int> Parse(string? value) {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var ports = new List<int>();
        foreach (var range in value.AsSpan().Split(',')) {
            if (TryParsePort(value.AsSpan()[range].Trim(), out var port)) ports.Add(port);
        }
        return Normalize(ports);
    }

    private static List<int> Normalize(IEnumerable<int> ports) {
        var seen = new SortedSet<int>();
        foreach (var port in ports) {
            if (port is > 0 and <= 65535) seen.Add(port);
        }
        return [.. seen];
    }

    private static bool TryParsePort(ReadOnlySpan<char> text, out int port) {
        port = 0;
        // Digits only. Integer parsing would otherwise accept a leading sign or thousands separator, and
        // "+9001" naming the same listener as "9001" is a second spelling of a canonical value.
        if (text.Length is 0 or > 5) return false;
        foreach (var c in text) {
            if (!char.IsAsciiDigit(c)) return false;
        }
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port)
            && port is > 0 and <= 65535;
    }
}
