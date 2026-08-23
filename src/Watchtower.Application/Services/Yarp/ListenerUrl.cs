namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// Reading a Kestrel listener URL. Shared by the ingress projection, the listener state and the status
/// surface, because all three have to agree on what "the port" of an endpoint is.
/// </summary>
public static class ListenerUrl {
    /// <summary>
    /// The port in a Kestrel URL, whether it is a bound address (<c>http://[::]:8080</c>) or a configured
    /// wildcard (<c>http://+:8080</c>) — neither of which <see cref="Uri"/> will parse. A URL that names no
    /// port falls back to its scheme's default, because that is the port it will bind.
    /// </summary>
    public static int? PortOf(string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return null;
        var scheme = url[..schemeEnd];
        var rest = url[(schemeEnd + 3)..];
        var end = rest.IndexOf('/');
        var authority = end < 0 ? rest : rest[..end];

        // IPv6 literals keep their brackets, so the port separator is the last colon outside them.
        var colon = authority.LastIndexOf(':');
        var closing = authority.LastIndexOf(']');
        if (colon <= closing)
            return scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ? 443 : 80;

        return int.TryParse(authority[(colon + 1)..], out var port) && port > 0 ? port : null;
    }
}
