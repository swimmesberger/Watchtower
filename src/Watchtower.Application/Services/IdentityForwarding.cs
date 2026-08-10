using Watchtower.Application.Entities;

namespace Watchtower.Application.Services;

/// <summary>
/// The single source for how Watchtower forwards identity to a protected upstream and — separately — which
/// inbound headers it neutralizes first. Two distinct sets live here and must not be conflated:
/// <list type="bullet">
///   <item><description>
///     <see cref="CopyHeaderNames"/> — what verify <em>sets</em> and Caddy <em>copies</em> onto the proxied
///     request (the JWT always, plus the route mode's plaintext names).
///   </description></item>
///   <item><description>
///     <see cref="StripHeaderNames"/> — the defense-in-depth deny-list Caddy strips from the inbound request
///     on every protected route. It is a deliberate <em>superset</em> of everything we forward.
///   </description></item>
/// </list>
/// Both ends read from here so the sets cannot drift; the invariant is that
/// <see cref="CopyHeaderNames"/> for every mode is a subset of <see cref="StripHeaderNames"/> — every name we
/// copy is also stripped, so nothing we forward is client-spoofable (design.md §2.3).
/// </summary>
/// <remarks>
/// Why the strip set is broader than what we forward: <c>forward_auth</c>'s <c>copy_headers</c> governs only
/// the verify <em>response</em> — every <em>other</em> client header passes through to the upstream
/// untouched. By adopting the <c>Remote-*</c> and <c>X-Auth-Request-*</c> names we are pointing group-aware,
/// trusted-header apps (Grafana, Nextcloud, …) at this proxy — apps that will read <c>Remote-Groups</c> or
/// <c>X-Auth-Request-Groups</c> and map them to roles. If we stripped only the three identity names we
/// currently populate, a low-privilege authenticated user could send <c>Remote-Groups: admins</c> and have
/// it reach such an app as authoritative. So <see cref="StripHeaderNames"/> neutralizes the <em>full</em>
/// documented identity+authz vocabulary of both ecosystems whose names we adopted — including the group and
/// access-token headers and oauth2-proxy's <c>--pass-user-headers</c> <c>X-Forwarded-*</c> identity family —
/// regardless of which subset we happen to populate today.
/// <para>
/// The transport headers <c>X-Forwarded-For</c>/<c>-Proto</c>/<c>-Host</c> are deliberately <em>not</em>
/// stripped: Caddy sets those legitimately for the upstream. The list is enumerated by exact name, never an
/// <c>X-Forwarded-*</c> prefix, precisely so those survive.
/// </para>
/// <para>
/// Groups are Phase 2. When group forwarding lands, its name must be added to <see cref="CopyHeaderNames"/>
/// — it is already present in <see cref="StripHeaderNames"/>. Any new forwarded name must appear in both.
/// </para>
/// </remarks>
public static class IdentityForwarding {
    // ── Remote-* (Authelia / Traefik) ──────────────────────────────────────────

    /// <summary>Authelia/Traefik verified user name.</summary>
    public const string RemoteUser = "Remote-User";

    /// <summary>Authelia/Traefik display name — we have no separate display name, so this carries the user name too.</summary>
    public const string RemoteName = "Remote-Name";

    /// <summary>Authelia/Traefik verified email, forwarded only when the account has one.</summary>
    public const string RemoteEmail = "Remote-Email";

    /// <summary>Authelia/Traefik group list. Not forwarded (groups are Phase 2), but stripped — see remarks.</summary>
    public const string RemoteGroups = "Remote-Groups";

    // ── X-Auth-Request-* (oauth2-proxy --set-xauthrequest) ─────────────────────

    /// <summary>oauth2-proxy verified user name.</summary>
    public const string AuthRequestUser = "X-Auth-Request-User";

    /// <summary>oauth2-proxy preferred username — the user name, since we have no separate display name.</summary>
    public const string AuthRequestPreferredUsername = "X-Auth-Request-Preferred-Username";

    /// <summary>oauth2-proxy verified email, forwarded only when the account has one.</summary>
    public const string AuthRequestEmail = "X-Auth-Request-Email";

    /// <summary>oauth2-proxy group list. Not forwarded (Phase 2), but stripped — see remarks.</summary>
    public const string AuthRequestGroups = "X-Auth-Request-Groups";

    /// <summary>oauth2-proxy upstream access token. Never forwarded, but stripped so a client cannot fake one.</summary>
    public const string AuthRequestAccessToken = "X-Auth-Request-Access-Token";

    // ── X-Forwarded-* identity family (oauth2-proxy --pass-user-headers) ───────
    // The IDENTITY members only. The transport headers X-Forwarded-For/-Proto/-Host are intentionally absent.

    /// <summary>oauth2-proxy pass-user-headers user name. Not forwarded, but stripped — see remarks.</summary>
    public const string ForwardedUser = "X-Forwarded-User";

    /// <summary>oauth2-proxy pass-user-headers email. Not forwarded, but stripped — see remarks.</summary>
    public const string ForwardedEmail = "X-Forwarded-Email";

    /// <summary>oauth2-proxy pass-user-headers group list. Not forwarded, but stripped — see remarks.</summary>
    public const string ForwardedGroups = "X-Forwarded-Groups";

    /// <summary>oauth2-proxy pass-user-headers preferred username. Not forwarded, but stripped — see remarks.</summary>
    public const string ForwardedPreferredUsername = "X-Forwarded-Preferred-Username";

    /// <summary>The plaintext names a <see cref="IdentityHeaderMode.Remote"/> route forwards, in order.</summary>
    private static readonly string[] RemoteNames = [RemoteUser, RemoteName, RemoteEmail];

    /// <summary>The plaintext names an <see cref="IdentityHeaderMode.AuthRequest"/> route forwards, in order.</summary>
    private static readonly string[] AuthRequestNames = [AuthRequestUser, AuthRequestPreferredUsername, AuthRequestEmail];

    /// <summary>
    /// The defense-in-depth deny-list: every identity/authz header name the two ecosystems we adopted define,
    /// stripped from the inbound request on <em>every</em> protected route (all modes, including
    /// <see cref="IdentityHeaderMode.None"/>). Broader than what we forward on purpose — see the type remarks
    /// for why <c>Remote-Groups</c> and friends must be neutralized even though we never set them. Enumerated
    /// by exact name so the legitimate transport <c>X-Forwarded-For</c>/<c>-Proto</c>/<c>-Host</c> survive.
    /// </summary>
    public static readonly string[] StripHeaderNames = [
        RouteAccessPolicy.JwtHeaderName,
        // Authelia / Traefik.
        RemoteUser, RemoteName, RemoteEmail, RemoteGroups,
        // oauth2-proxy --set-xauthrequest.
        AuthRequestUser, AuthRequestPreferredUsername, AuthRequestEmail, AuthRequestGroups, AuthRequestAccessToken,
        // oauth2-proxy --pass-user-headers (identity members only; not the transport X-Forwarded headers).
        ForwardedUser, ForwardedEmail, ForwardedGroups, ForwardedPreferredUsername,
    ];

    /// <summary>
    /// The header names copied back onto the proxied request for a route in <paramref name="mode"/>: the JWT
    /// header always, plus that mode's plaintext names. Always a subset of <see cref="StripHeaderNames"/>, so
    /// every copied name is also stripped.
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
