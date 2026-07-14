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

    public RouteStatus Status { get; set; } = RouteStatus.Pending;
    /// <summary>Human-readable detail for the current status (e.g. an error reason).</summary>
    public string? StatusDetail { get; set; }
    /// <summary>Certificate expiry as reported by the proxy, when known.</summary>
    public DateTimeOffset? CertNotAfter { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
