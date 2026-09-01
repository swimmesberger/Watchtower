using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;

namespace Watchtower.Application.Entities;

/// <summary>Provisioning state of a route's public domain (chiefly its TLS certificate).</summary>
public enum RouteStatus {
    /// <summary>Route created; the proxy has not yet been reconciled for it.</summary>
    Pending,
    /// <summary>The domain does not yet resolve to this host — DNS must be pointed here before a cert can issue.</summary>
    AwaitingDns,
    /// <summary>The proxy is serving the domain (certificate issued).</summary>
    Active,
    /// <summary>Provisioning failed; see <see cref="Route.StatusDetail"/>.</summary>
    Error,
}

/// <summary>Whether a domain is a subdomain the operator controls or a customer-owned custom domain.</summary>
public enum DomainKind {
    /// <summary>A subdomain under a domain the operator controls (e.g. <c>tenant1.example.com</c>).</summary>
    Managed,
    /// <summary>A customer-owned domain pointed at this host (e.g. <c>app.customer.com</c>).</summary>
    Custom,
}

/// <summary>Who may reach the app behind a route (docs/central-auth/design.md §3).</summary>
public enum AccessMode {
    /// <summary>No access control — the proxy forwards every request, as it always has.</summary>
    Public,
    /// <summary>Any signed-in Watchtower user may enter; anonymous requests are sent to the central login.</summary>
    Authenticated,
    /// <summary>
    /// Only the subjects a <see cref="RouteAccessGrant"/> names for this route may enter: an account
    /// granted directly, or any member of a granted <see cref="Group"/>. A member holds no grant of their
    /// own, so membership is resolved per request rather than materialised into grant rows.
    /// </summary>
    Restricted,
}

/// <summary>
/// Which plaintext identity headers the proxy forwards to a protected upstream (docs/central-auth/design.md
/// §2.3). The signed <c>X-Watchtower-Jwt</c> assertion is <em>always</em> forwarded and is the source of
/// truth; plaintext headers are a convenience for off-the-shelf apps that cannot validate a JWT, so they are
/// opt-in per route and use ecosystem-standard names rather than a bespoke one no app recognises.
/// </summary>
public enum IdentityHeaderMode {
    /// <summary>JWT only (the default): no plaintext identity header is forwarded. The safest choice.</summary>
    None,
    /// <summary>Authelia/Traefik <c>Remote-User</c>/<c>Remote-Name</c>/<c>Remote-Email</c> names.</summary>
    Remote,
    /// <summary>oauth2-proxy <c>X-Auth-Request-User</c>/<c>-Preferred-Username</c>/<c>-Email</c> names.</summary>
    AuthRequest,
    /// <summary>
    /// Cloudflare Access <c>Cf-Access-Authenticated-User-Email</c> plus the Watchtower assertion
    /// duplicated under <c>Cf-Access-Jwt-Assertion</c>. For apps written against Cloudflare's header
    /// contract, so the same stack runs unchanged behind Cloudflare Access or behind integrated auth —
    /// an app that verifies the assertion cryptographically re-points its JWKS/issuer configuration at
    /// Watchtower (<c>/api/auth/jwks</c>) instead of <c>{team}.cloudflareaccess.com</c>; the header
    /// names stay identical.
    /// </summary>
    Cloudflare,
}

/// <summary>
/// What a route's hostname is served by (ADR-0023). One column decides whether a row describes a
/// forwarded application or a hostname Watchtower answers on itself, and everything downstream —
/// the site projection, the check constraint, the realm lookup, the UI — reads it rather than
/// inferring the answer from which other columns happen to be null.
/// </summary>
public enum RouteTarget {
    /// <summary>A service inside a <see cref="Stack"/>: the proxy forwards to its container.</summary>
    Service,
    /// <summary>
    /// Watchtower itself. The proxy serves this instance's own UI and API on the hostname — its
    /// management surface, and (when the route is its realm's login route) that realm's login page.
    /// Such a row carries a <see cref="RealmId"/> instead of a <see cref="StackId"/> and is always
    /// <see cref="AccessMode.Public"/>: Watchtower authenticates its visitors natively, so route
    /// access control has nothing to add and putting a login page behind the gate that redirects to
    /// it would be a closed loop.
    /// </summary>
    Watchtower,
}

/// <summary>
/// How a route is addressed (ADR-0033). A route is reached either by the hostname a visitor types or by
/// the port a listener of its own is bound to, and the two are different rows with different required
/// columns — which is what <c>ck_routes_binding</c> makes structural.
/// </summary>
public enum RouteBinding {
    /// <summary>
    /// A public hostname, matched from the <c>Host</c> header on the shared ingress listeners and given a
    /// certificate by ACME. The original kind, and the default.
    /// </summary>
    Domain,
    /// <summary>
    /// A dedicated TLS port on this host, with no hostname at all: a LAN address a client reaches by
    /// number sends no usable SNI, so the listener is what identifies the service and the certificate
    /// comes from Watchtower's own internal CA rather than from a public one. Always a
    /// <see cref="RouteTarget.Service"/> route, always <see cref="AccessMode.Public"/>, always TLS —
    /// there is no hostname for a login redirect to come back to, and a plain-HTTP port route would be a
    /// second, weaker way into an app that has one.
    /// </summary>
    Port,
}

/// <summary>
/// A public domain the reverse proxy terminates TLS for. A <see cref="RouteTarget.Service"/> route
/// forwards it to a service inside a <see cref="Stack"/>; a <see cref="RouteTarget.Watchtower"/>
/// route is served by Watchtower itself (ADR-0023). The set of routes is the authoritative source
/// for the generated proxy configuration and for which hostnames serve Watchtower.
/// </summary>
public sealed class Route : IHasXmin {
    public int Id { get; set; }

    /// <summary>
    /// Whether this hostname forwards to a stack service or is served by Watchtower itself. Immutable
    /// after creation: the two kinds are different rows with different required columns, and switching
    /// between them in place would mean silently re-pointing a live hostname at something else.
    /// </summary>
    public RouteTarget Target { get; set; } = RouteTarget.Service;

    /// <summary>
    /// Whether this route is addressed by hostname or by a port of its own. Immutable after creation, for
    /// the same reason <see cref="Target"/> is: the two kinds fill different columns, and switching one in
    /// place would move a live address rather than edit it.
    /// </summary>
    public RouteBinding Binding { get; set; } = RouteBinding.Domain;

    /// <summary>
    /// The stack whose service this route targets. Null exactly for a
    /// <see cref="RouteTarget.Watchtower"/> route, which has no upstream to forward to — the check
    /// constraint <c>ck_routes_target</c> is what makes "exactly" true.
    /// </summary>
    public int? StackId { get; set; }
    public Stack? Stack { get; set; }

    /// <summary>
    /// The realm whose Watchtower surface this hostname serves — its login page when the realm names
    /// this route as its <see cref="Realm.LoginRouteId"/>, and its portal either way. Set exactly for
    /// a <see cref="RouteTarget.Watchtower"/> route; a <see cref="RouteTarget.Service"/> route
    /// inherits its realm from its stack's category instead (docs/central-auth/design.md §13).
    /// </summary>
    public int? RealmId { get; set; }
    public Realm? Realm { get; set; }

    /// <summary>
    /// The public hostname, e.g. <c>app.example.com</c>. Unique across all routes that have one; null
    /// exactly for a <see cref="RouteBinding.Port"/> route, which is addressed by port instead — the
    /// check constraint <c>ck_routes_binding</c> is what makes "exactly" true.
    /// </summary>
    public string? Domain { get; set; }

    /// <summary>
    /// The host port this route's own TLS listener is bound to, unique across all routes that have one.
    /// Set exactly for a <see cref="RouteBinding.Port"/> route.
    /// </summary>
    /// <remarks>
    /// One port per route rather than one shared port with virtual hosting: a client reaching a bare
    /// address sends no SNI and no meaningful <c>Host</c>, so the port is the only thing that can say
    /// which service a connection wants. The port has to be published on Watchtower's own container as
    /// well — nothing here can do that, and a route whose port is not published simply never accepts.
    /// </remarks>
    public int? ListenPort { get; set; }
    /// <summary>
    /// The compose service within the stack to forward to (its container is joined to the edge
    /// network). Empty on a <see cref="RouteTarget.Watchtower"/> route, which forwards nowhere.
    /// </summary>
    public required string ServiceName { get; set; }
    /// <summary>
    /// The container-side port the service listens on. Zero on a <see cref="RouteTarget.Watchtower"/>
    /// route — where Watchtower reaches itself is <see cref="Services.ProxySiteProjection.SelfPort"/>,
    /// not a per-row choice.
    /// </summary>
    public int ContainerPort { get; set; }
    /// <summary>When true the proxy terminates HTTPS and auto-manages a certificate; when false it serves plain HTTP.</summary>
    public bool TlsEnabled { get; set; } = true;
    /// <summary>Marks the canonical domain for the stack (others may redirect to it).</summary>
    public bool IsPrimary { get; set; }
    public DomainKind Kind { get; set; } = DomainKind.Managed;

    /// <summary>
    /// Access policy for this app. <see cref="AccessMode.Public"/> (the default) keeps the proxy
    /// behaviour unchanged; the other modes make the proxy verify each request with Watchtower first.
    /// </summary>
    public AccessMode AccessMode { get; set; } = AccessMode.Public;

    /// <summary>
    /// Which plaintext identity headers reach the upstream on a verified request.
    /// <see cref="IdentityHeaderMode.None"/> (the default) forwards only the signed <c>X-Watchtower-Jwt</c>
    /// assertion; the other modes additionally forward ecosystem-standard plaintext headers for apps that
    /// read a username header instead of validating the JWT.
    /// </summary>
    public IdentityHeaderMode IdentityHeaderMode { get; set; } = IdentityHeaderMode.None;

    /// <summary>
    /// Request-path prefixes exempt from access control even when the route is protected, one per
    /// line (e.g. webhook receivers and health endpoints that non-browser clients call). Null or
    /// empty means every path is checked.
    /// </summary>
    public string? BypassPaths { get; set; }

    public RouteStatus Status { get; set; } = RouteStatus.Pending;
    /// <summary>Human-readable detail for the current status (e.g. an error reason).</summary>
    public string? StatusDetail { get; set; }
    /// <summary>Certificate expiry as reported by the proxy, when known.</summary>
    public DateTimeOffset? CertNotAfter { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// Mapped by <c>XminConcurrency.UseXminAsConcurrencyToken</c>; see <see cref="IHasXmin"/> for why
    /// this is a real property rather than an EF shadow property. Last, because it is the database's
    /// bookkeeping rather than part of what this entity means.
    /// </remarks>
    public uint Xmin { get; private set; }

    /// <summary>
    /// How this route is named in an audit target, a log line or an error message. The hostname where
    /// there is one, and <c>port {n}</c> otherwise — a port route has no name of its own, and the number
    /// is the only thing an operator would recognise it by.
    /// </summary>
    /// <remarks>
    /// Not mapped: it is a rendering of two stored columns, and a route is looked up by whichever of them
    /// its binding fills rather than by this.
    /// </remarks>
    [NotMapped]
    public string DisplayAddress =>
        Domain ?? (ListenPort is { } port ? $"port {port.ToString(CultureInfo.InvariantCulture)}" : $"route {Id}");
}
