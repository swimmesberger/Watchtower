using System.Net;
using System.Net.Sockets;
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

    /// <summary>The file name the PEM download is offered under.</summary>
    public const string DownloadFileName = "watchtower-internal-ca.crt";

    /// <summary>
    /// The file name the DER download is offered under. A different extension because the import
    /// dialogs that want DER key off it — a <c>.crt</c> holding binary is what they refuse.
    /// </summary>
    public const string DownloadFileNameDer = "watchtower-internal-ca.cer";

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
    /// <para>
    /// Addresses come back in the form a certificate can hold — an IPv6 scope id (<c>fe80::1%3</c>) is
    /// dropped, because the encoding has nowhere to put one and everything downstream compares what was
    /// configured against what was issued.
    /// </para>
    /// <para>
    /// An IPv4 address has to be written in its canonical dotted-quad form. The framework's parser still
    /// honours the inet_aton spellings, so <c>192.168.001.010</c> would quietly become 192.168.1.8 and the
    /// leaf would name an address nobody typed — which is the "junk fails the whole parse" contract above,
    /// inverted. A host name may carry the trailing dot of its fully-qualified form, as
    /// <see cref="Acme.DesiredHosts.TryNormalize"/> lets a domain route's host.
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
                     [',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            // An IP first: 192.168.1.10 would also satisfy the DNS-name grammar, and reading it as a
            // host name would put it in the wrong kind of SAN — where no client looks for it.
            if (IPAddress.TryParse(entry, out var ip)) {
                // IPAddress still accepts the inet_aton spellings — octal (`192.168.001.010` is
                // 192.168.1.8), hex (`0x7f.1`), a bare integer (`2130706433`), a three-part address
                // (`10.0.1`). Every one of them is a typo far more often than an intention, and issuing
                // for the address it silently means would put a name in the certificate that the operator
                // never wrote. Round-tripped rather than pattern-matched, so the rule is exactly "the
                // canonical spelling of what this means is what you typed". IPv4 only: an IPv6 address
                // has many correct spellings (`FE80::1`, `fe80:0:0:0:0:0:0:1`), and refusing those would
                // be refusing valid input.
                if (ip.AddressFamily == AddressFamily.InterNetwork
                    && !string.Equals(ip.ToString(), entry, StringComparison.Ordinal)) {
                    reason = $"'{entry}' is not a plain dotted-quad address — it would mean {ip}. "
                        + "Write the address out in full.";
                    return false;
                }
                // Rebuilt from the address bytes, which drops any IPv6 scope id (`fe80::1%3`). A
                // certificate cannot carry one — the SAN builder encodes the 16 address bytes and
                // nothing else — so keeping it here would make the held certificate look like it named
                // something different from the configuration on every single pass, and reissue forever.
                // Unconditional rather than guarded on the address family, because reading ScopeId off
                // an IPv4 address throws and reconstructing one costs a four-byte copy.
                ip = new IPAddress(ip.GetAddressBytes());
                if (seenAddresses.Add(ip.ToString())) addresses.Add(ip);
                continue;
            }

            // One trailing dot is the fully-qualified spelling of the same name, and
            // DesiredHosts.TryNormalize accepts it for a domain route — so refusing it here would make
            // `nas.lan.` valid in one field of the Settings page and junk in another. Exactly one: a
            // second dot leaves an empty label, which NormalizeHost refuses as it should.
            var candidate = entry.EndsWith('.') ? entry[..^1] : entry;

            string name;
            try {
                // The same validation the certificate store applies to a host, so a name that parses
                // here is a name a leaf can actually be stored and served under.
                name = CertificateStore.NormalizeHost(candidate);
            } catch (ArgumentException) {
                reason = $"'{entry}' is neither a host name nor an IP address.";
                return false;
            }
            if (seenNames.Add(name)) names.Add(name);
        }

        return true;
    }
}
