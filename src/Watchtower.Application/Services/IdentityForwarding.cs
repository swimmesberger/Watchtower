using Watchtower.Application.Entities;

namespace Watchtower.Application.Services;

/// <summary>
/// The single source for what identity Watchtower forwards to a protected upstream, read by both ends that
/// have to agree on it: the verify endpoint (which <em>sets</em> the headers) and the Caddy config builder
/// (which <em>strips</em> the inbound copies and <em>copies</em> the verified ones back out). A name that
/// were copied but not stripped would be client-spoofable, which is the failure mode design.md §2.3 exists
/// to prevent — so the two sides derive from this one place rather than each holding their own list.
/// </summary>
/// <remarks>
/// The signed <c>X-Watchtower-Jwt</c> assertion (<see cref="RouteAccessPolicy.JwtHeaderName"/>) is the
/// source of truth and is always forwarded for a protected route. Plaintext identity headers are a
/// convenience for off-the-shelf apps that cannot validate a JWT; they are opt-in per route
/// (<see cref="IdentityHeaderMode"/>) and use the names the surrounding ecosystem already recognises —
/// Authelia/Traefik's <c>Remote-*</c> and oauth2-proxy's <c>X-Auth-Request-*</c> — rather than a bespoke
/// name no app reads. Groups are Phase 2 and deliberately absent here.
/// </remarks>
public static class IdentityForwarding {
    // ── Remote-* (Authelia / Traefik) ──────────────────────────────────────────

    /// <summary>Authelia/Traefik verified user name.</summary>
    public const string RemoteUser = "Remote-User";

    /// <summary>Authelia/Traefik display name — we have no separate display name, so this carries the user name too.</summary>
    public const string RemoteName = "Remote-Name";

    /// <summary>Authelia/Traefik verified email, forwarded only when the account has one.</summary>
    public const string RemoteEmail = "Remote-Email";

    // ── X-Auth-Request-* (oauth2-proxy) ────────────────────────────────────────

    /// <summary>oauth2-proxy verified user name.</summary>
    public const string AuthRequestUser = "X-Auth-Request-User";

    /// <summary>oauth2-proxy preferred username — the user name, since we have no separate display name.</summary>
    public const string AuthRequestPreferredUsername = "X-Auth-Request-Preferred-Username";

    /// <summary>oauth2-proxy verified email, forwarded only when the account has one.</summary>
    public const string AuthRequestEmail = "X-Auth-Request-Email";

    /// <summary>The plaintext names a <see cref="IdentityHeaderMode.Remote"/> route forwards, in order.</summary>
    private static readonly string[] RemoteNames = [RemoteUser, RemoteName, RemoteEmail];

    /// <summary>The plaintext names an <see cref="IdentityHeaderMode.AuthRequest"/> route forwards, in order.</summary>
    private static readonly string[] AuthRequestNames = [AuthRequestUser, AuthRequestPreferredUsername, AuthRequestEmail];

    /// <summary>
    /// The union of the JWT header and every plaintext name across <em>all</em> modes. This is the
    /// defense-in-depth strip set: the generated Caddy config strips every one of these from the inbound
    /// request for <em>every</em> protected route, whatever that route's mode — so nothing a client sends
    /// under any of these names can survive to the upstream, and a route can only ever receive the values
    /// Watchtower itself wrote.
    /// </summary>
    public static readonly string[] AllForwardableHeaderNames =
        [RouteAccessPolicy.JwtHeaderName, .. RemoteNames, .. AuthRequestNames];

    /// <summary>
    /// The header names copied back onto the proxied request for a route in <paramref name="mode"/>: the JWT
    /// header always, plus that mode's plaintext names. Always a subset of
    /// <see cref="AllForwardableHeaderNames"/>, so every copied name is also stripped.
    /// </summary>
    public static IReadOnlyList<string> CopyHeaderNames(IdentityHeaderMode mode) => mode switch {
        IdentityHeaderMode.Remote => [RouteAccessPolicy.JwtHeaderName, .. RemoteNames],
        IdentityHeaderMode.AuthRequest => [RouteAccessPolicy.JwtHeaderName, .. AuthRequestNames],
        // None (and any unknown value, fail-closed): the JWT is the only thing forwarded.
        _ => [RouteAccessPolicy.JwtHeaderName],
    };

    /// <summary>
    /// The plaintext identity headers to set on a verified request for <paramref name="mode"/>, as
    /// name/value pairs mapped from the account's <paramref name="userName"/> and <paramref name="email"/>.
    /// The email entry is omitted when the account has none; <see cref="IdentityHeaderMode.None"/> yields
    /// nothing (the JWT, set separately, is the whole story). The caller is still responsible for filtering
    /// each value for header-safety before setting it.
    /// </summary>
    public static IEnumerable<(string Name, string Value)> PlaintextHeaders(
        IdentityHeaderMode mode, string userName, string? email) {
        switch (mode) {
            case IdentityHeaderMode.Remote:
                yield return (RemoteUser, userName);
                yield return (RemoteName, userName);
                if (!string.IsNullOrWhiteSpace(email)) yield return (RemoteEmail, email);
                break;
            case IdentityHeaderMode.AuthRequest:
                yield return (AuthRequestUser, userName);
                yield return (AuthRequestPreferredUsername, userName);
                if (!string.IsNullOrWhiteSpace(email)) yield return (AuthRequestEmail, email);
                break;
        }
    }
}
