using System.Net;
using Watchtower.Application.Services.Acme;

namespace Watchtower.Application.Services.InternalCa;

/// <summary>
/// The fixed names the internal CA is addressed by, and the parser for the LAN names its leaf is
/// issued for — shared by the settings validation and the issuance path so a value the Settings page
/// accepted can never be one the issuer refuses.
/// </summary>
public static class InternalCaNames {
    /// <summary>
    /// The store key the one shared LAN leaf is held under. Not a name anything resolves: the leaf is
    /// selected by listening port rather than by SNI, and <c>.invalid</c> is reserved by RFC 6761 so it
    /// cannot collide with a routed domain or be issued for by a public CA.
    /// </summary>
    public const string SharedLeafHost = "internal-lan.watchtower.invalid";

    /// <summary>The <see cref="Entities.InternalCa.Name"/> of the only CA v1 creates.</summary>
    public const string CaRowName = "default";

    /// <summary>The <c>KeyProtector</c> purpose the root's private key is encrypted under.</summary>
    public const string KeyPurpose = "internal-ca";

    /// <summary>
    /// Where the root certificate is downloaded from. A constant rather than a literal in two places:
    /// the API surfaces it so a client never builds the URL itself, and the endpoint that serves it
    /// lives in another project.
    /// </summary>
    public const string DownloadPath = "/api/proxy/internal-ca.crt";

    /// <summary>The file name the download is offered under.</summary>
    public const string DownloadFileName = "watchtower-internal-ca.crt";

    /// <summary>
    /// Reads the configured LAN names into the two kinds of subject alternative name a leaf can carry.
    /// </summary>
    /// <remarks>
    /// Both forms matter and neither substitutes for the other: a browser asked for
    /// <c>https://nas.lan:9001</c> matches a DNS SAN, and one asked for <c>https://192.168.1.10:9001</c>
    /// matches only an IP SAN — which is the whole reason an operator on a LAN needs to list both.
    /// <para>
    /// Entries are separated by commas or newlines, deduplicated, and kept in the order they were
    /// written. A junk entry fails the whole parse rather than being dropped: silently issuing for four
    /// of five names would surface as one device that cannot reach the service, weeks later.
    /// </para>
    /// </remarks>
    /// <param name="reason">Why the parse failed, naming the offending entry; null on success.</param>
    /// <returns>Whether every entry was a DNS name or an IP address. An empty value parses to nothing.</returns>
    public static bool TryParseLanNames(
        string? raw,
        out IReadOnlyList<string> dnsNames,
        out IReadOnlyList<IPAddress> ips,
        out string? reason) {
        var names = new List<string>();
        var addresses = new List<IPAddress>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dnsNames = names;
        ips = addresses;
        reason = null;

        foreach (var entry in (raw ?? "").Split(
                     [',', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            // An IP first: 192.168.1.10 would also satisfy the DNS-name grammar, and reading it as a
            // host name would put it in the wrong kind of SAN — where no client looks for it.
            if (IPAddress.TryParse(entry, out var ip)) {
                if (seenAddresses.Add(ip.ToString())) addresses.Add(ip);
                continue;
            }

            string name;
            try {
                // The same validation the certificate store applies to a host, so a name that parses
                // here is a name a leaf can actually be stored and served under.
                name = CertificateStore.NormalizeHost(entry);
            } catch (ArgumentException) {
                reason = $"'{entry}' is neither a host name nor an IP address.";
                return false;
            }
            if (seenNames.Add(name)) names.Add(name);
        }

        return true;
    }
}
