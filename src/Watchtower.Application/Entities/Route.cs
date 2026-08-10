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
    /// <summary>Only users holding a <see cref="RouteAccessGrant"/> for this route may enter.</summary>
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
}

/// <summary>
/// A public domain that the built-in reverse proxy (Caddy) terminates TLS for and forwards to a
/// service inside a <see cref="Stack"/>. The set of routes is the authoritative source for the
/// generated proxy configuration.
/// </summary>
public sealed class Route {
    public int Id { get; set; }
    /// <summary>The stack whose service this route targets.</summary>
    public int StackId { get; set; }
    public Stack? Stack { get; set; }

    /// <summary>The public hostname, e.g. <c>app.example.com</c>. Unique across all routes.</summary>
    public required string Domain { get; set; }
    /// <summary>The compose service within the stack to forward to (its container is joined to the edge network).</summary>
    public required string ServiceName { get; set; }
    /// <summary>The container-side port the service listens on.</summary>
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
}
