using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Watchtower.Application.Services.Acme;

/// <summary>The two sides of a desired-host change: what appeared and what went away.</summary>
public readonly record struct HostSetDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Removed) {
    /// <summary>Whether anything changed at all — the common case is that nothing did.</summary>
    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0;
}

/// <summary>
/// Normalizes and validates the host names the in-process proxy asks for certificates for — ADR-0017
/// (forthcoming). Pure and static, so the rules can be read and tested in one place instead of being
/// rediscovered at each of the three points that need them (route validation, the desired-host set, the
/// certificate store's directory names).
/// </summary>
/// <remarks>
/// The rules are the intersection of what a CA will issue for and what the store can safely write to
/// disk. Both are stricter than "a string with dots in it", and getting either wrong is expensive: a
/// name the CA rejects burns a validation failure against a rate limit, and a name the store would
/// write is a path.
/// <para>
/// Non-ASCII is rejected with a punycode hint rather than converted. <c>System.Globalization.IdnMapping</c>
/// is the obvious tool and it is not available: the API host builds with <c>InvariantGlobalization</c>,
/// where IDN mapping throws. Telling an operator to paste the <c>xn--</c> form is a worse experience than
/// converting for them, and a far better one than a mystery failure at issuance time.
/// </para>
/// </remarks>
public static class DesiredHosts {
    /// <summary>The longest a DNS name may be, dots included.</summary>
    private const int MaxNameLength = 253;

    /// <summary>The longest a single label may be.</summary>
    private const int MaxLabelLength = 63;

    /// <summary>
    /// Normalizes <paramref name="raw"/> to the canonical form everything downstream uses — trimmed,
    /// lowercase, no trailing root dot — or explains why it is not a host Watchtower can get a
    /// certificate for.
    /// </summary>
    /// <param name="host">The canonical name on success; empty otherwise.</param>
    /// <param name="rejectReason">A sentence to show the operator on failure; null on success.</param>
    public static bool TryNormalize(
        string? raw, out string host, [NotNullWhen(false)] out string? rejectReason) {
        host = "";
        rejectReason = null;

        var value = raw?.Trim() ?? "";
        if (value.Length == 0) {
            rejectReason = "A domain is required.";
            return false;
        }

        // One trailing dot is the fully-qualified form of the same name; more than one is not a name.
        if (value.EndsWith('.')) value = value[..^1];
        value = value.ToLowerInvariant();

        if (value.Length == 0) {
            rejectReason = "A domain is required.";
            return false;
        }
        if (value.Contains('*')) {
            // Wildcards need DNS-01, which needs write access to the operator's zone — see ADR-0017.
            rejectReason = "Wildcard domains are not supported; list each host name explicitly.";
            return false;
        }
        if (value.Any(char.IsWhiteSpace)) {
            rejectReason = "A domain cannot contain spaces.";
            return false;
        }
        if (value.Contains('/') || value.Contains('\\')) {
            rejectReason = "Enter the host name only, without a scheme or path.";
            return false;
        }
        if (value.Contains(':')) {
            rejectReason = "Enter the host name only, without a port.";
            return false;
        }
        if (value.Any(c => !char.IsAscii(c))) {
            rejectReason = "Enter the punycode (xn--…) form of an internationalized domain name.";
            return false;
        }
        // Checked before the label walk so the message names the actual problem rather than "empty label".
        if (value.Contains("..", StringComparison.Ordinal)) {
            rejectReason = "A domain cannot contain an empty label ('..').";
            return false;
        }
        if (IPAddress.TryParse(value, out _) || IPAddress.TryParse(value.Trim('[', ']'), out _)) {
            // A CA will not issue for an IP over HTTP-01, and the routing table keys on names.
            rejectReason = "Enter a domain name, not an IP address.";
            return false;
        }
        if (value.Length > MaxNameLength) {
            rejectReason = $"A domain cannot be longer than {MaxNameLength} characters.";
            return false;
        }

        foreach (var label in value.Split('.')) {
            if (label.Length == 0) {
                rejectReason = "A domain cannot contain an empty label ('..').";
                return false;
            }
            if (label.Length > MaxLabelLength) {
                rejectReason = $"'{label}' is longer than the {MaxLabelLength} characters a domain label may have.";
                return false;
            }
            if (label[0] == '-' || label[^1] == '-') {
                rejectReason = $"'{label}' cannot start or end with '-'.";
                return false;
            }
            foreach (var c in label)
                if (!char.IsAsciiLetterOrDigit(c) && c != '-') {
                    rejectReason = $"'{value}' may only contain letters, digits, '-' and '.'.";
                    return false;
                }
        }

        host = value;
        return true;
    }

    /// <summary>
    /// What changed between two desired-host sets. Ordinal because both sides are already normalized —
    /// comparing them case-insensitively here would paper over a normalization bug rather than fix one.
    /// </summary>
    public static HostSetDiff Diff(IReadOnlySet<string> current, IReadOnlySet<string> next) {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(next);
        return new HostSetDiff(
            next.Where(h => !current.Contains(h)).ToArray(),
            current.Where(h => !next.Contains(h)).ToArray());
    }
}
