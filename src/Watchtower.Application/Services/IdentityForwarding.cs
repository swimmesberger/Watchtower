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
/// Group forwarding has since landed: <c>Remote-Groups</c> and <c>X-Auth-Request-Groups</c> are now in
/// <em>both</em> sets — their mode's <see cref="CopyHeaderNames"/> as well as
/// <see cref="StripHeaderNames"/> — which is the invariant every forwarded name has to satisfy. The
/// oauth2-proxy <c>--pass-user-headers</c> family (<c>X-Forwarded-User</c> and friends, including
/// <c>X-Forwarded-Groups</c>) remains strip-only: we adopted two vocabularies, not three, and a name we
/// never populate is one less thing an upstream can be confused by.
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

    /// <summary>Authelia/Traefik group list: the account's group names, comma-joined. Forwarded and stripped.</summary>
    public const string RemoteGroups = "Remote-Groups";

    // ── X-Auth-Request-* (oauth2-proxy --set-xauthrequest) ─────────────────────

    /// <summary>oauth2-proxy verified user name.</summary>
    public const string AuthRequestUser = "X-Auth-Request-User";

    /// <summary>oauth2-proxy preferred username — the user name, since we have no separate display name.</summary>
    public const string AuthRequestPreferredUsername = "X-Auth-Request-Preferred-Username";

    /// <summary>oauth2-proxy verified email, forwarded only when the account has one.</summary>
    public const string AuthRequestEmail = "X-Auth-Request-Email";

    /// <summary>oauth2-proxy group list: the account's group names, comma-joined. Forwarded and stripped.</summary>
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
    private static readonly string[] RemoteNames = [RemoteUser, RemoteName, RemoteEmail, RemoteGroups];

    /// <summary>The plaintext names an <see cref="IdentityHeaderMode.AuthRequest"/> route forwards, in order.</summary>
    private static readonly string[] AuthRequestNames = [
        AuthRequestUser, AuthRequestPreferredUsername, AuthRequestEmail, AuthRequestGroups];

    /// <summary>
    /// The separator both ecosystems use in their group headers, and the reason group names are constrained
    /// to a charset that excludes it at the point they are created (see the Groups module): a name carrying
    /// a comma would be read downstream as two group memberships the account does not have.
    /// </summary>
    public const char GroupSeparator = ',';

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
    /// name/value pairs mapped from the account's <paramref name="userName"/>, <paramref name="email"/> and
    /// <paramref name="groups"/>. The email entry is omitted when the account has none and the group entry
    /// when it is in none — an empty header is not the same statement as an absent one, and some upstreams
    /// read <c>Remote-Groups: </c> as membership of a group whose name is the empty string.
    /// <see cref="IdentityHeaderMode.None"/> yields nothing (the JWT, set separately, is the whole story).
    /// The caller is still responsible for filtering each value for header-safety before setting it.
    /// </summary>
    /// <param name="groups">
    /// The account's group names. Emitted comma-joined in ordinal order, so the value a given account
    /// produces does not depend on the order the membership rows happened to come back in.
    /// </param>
    public static IEnumerable<(string Name, string Value)> PlaintextHeaders(
        IdentityHeaderMode mode, string userName, string? email, IReadOnlyList<string> groups) {
        var groupList = FormatGroups(groups);
        switch (mode) {
            case IdentityHeaderMode.Remote:
                yield return (RemoteUser, userName);
                yield return (RemoteName, userName);
                if (!string.IsNullOrWhiteSpace(email)) yield return (RemoteEmail, email);
                if (groupList is not null) yield return (RemoteGroups, groupList);
                break;
            case IdentityHeaderMode.AuthRequest:
                yield return (AuthRequestUser, userName);
                yield return (AuthRequestPreferredUsername, userName);
                if (!string.IsNullOrWhiteSpace(email)) yield return (AuthRequestEmail, email);
                if (groupList is not null) yield return (AuthRequestGroups, groupList);
                break;
        }
    }

    /// <summary>
    /// The header form of a group list — sorted ordinal and comma-joined — or <see langword="null"/> when
    /// there is nothing to say.
    /// </summary>
    /// <remarks>
    /// A name that is not emittable is dropped rather than emitted, and dropping it is the conservative
    /// direction. No such name can exist through the Groups module — <c>GroupMapping.ValidateName</c>
    /// rejects exactly this set at creation — so meeting one means the row was written by something else,
    /// and this filter is the second half of that defense rather than the first. Two kinds are unusable,
    /// and the reason each is dropped <em>individually</em> is the same: the alternative is worse.
    /// <list type="bullet">
    ///   <item><description>
    ///     A name containing the separator would hand the upstream <em>two</em> memberships, one of which
    ///     the account does not have.
    ///   </description></item>
    ///   <item><description>
    ///     A name outside printable ASCII would fail the caller's header-safety filter — which judges the
    ///     <em>joined</em> value — and so cost the account every other group it is in, turning one bad row
    ///     into a silent, total loss of group identity. Dropping the one name keeps the rest forwardable.
    ///   </description></item>
    /// </list>
    /// Both survive as-is in the JWT's <c>groups</c> claim, which is JSON and has neither problem.
    /// </remarks>
    private static string? FormatGroups(IReadOnlyList<string> groups) {
        ArgumentNullException.ThrowIfNull(groups);
        var usable = groups
            .Where(g => !string.IsNullOrWhiteSpace(g) && IsEmittable(g))
            .OrderBy(g => g, StringComparer.Ordinal)
            .ToList();
        return usable.Count == 0 ? null : string.Join(GroupSeparator, usable);
    }

    /// <summary>
    /// Whether a group name can appear in a comma-joined header value: printable ASCII, no separator. The
    /// same rule <c>GroupMapping.ValidateName</c> applies at creation, restated here because this is the
    /// boundary where violating it does damage.
    /// </summary>
    private static bool IsEmittable(string name) {
        foreach (var c in name)
            if (c is < ' ' or > '~' || c == GroupSeparator) return false;
        return true;
    }
}
