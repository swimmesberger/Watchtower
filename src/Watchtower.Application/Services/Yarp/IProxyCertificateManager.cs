using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services.Yarp;

/// <summary>
/// The seam between the in-process proxy's control plane (which knows <em>which</em> hosts need a
/// certificate) and whatever obtains them — ADR-0017 (forthcoming). Split out so the provider's
/// lifecycle can land — and be tested — without an ACME client, and so the ACME implementation can be
/// swapped for a file-backed or operator-supplied one later without touching the provider.
/// </summary>
public interface IProxyCertificateManager {
    /// <summary>
    /// Replaces the set of hosts the proxy wants certificates for. Declarative, not incremental: the
    /// implementation issues what is missing and renews what is expiring. Explicitly <em>not</em> a
    /// delete trigger — a host dropping out of the set means "stop renewing it", because a route
    /// removed by mistake and put back must not have cost an issuance. Deleting is
    /// <see cref="ForgetHostAsync"/>'s job, and only the route-delete path calls it.
    /// </summary>
    void SetDesiredHosts(IReadOnlyCollection<string> hosts);

    /// <summary>
    /// Deletes whatever is held for a host — the route-delete path. Unlike
    /// <see cref="SetDesiredHosts"/> this THROWS on failure: the caller asked for a specific change
    /// and must be told when it did not happen.
    /// </summary>
    Task ForgetHostAsync(string host, CancellationToken ct);
}

/// <summary>
/// Accepts the desired-host set and forgets nothing — the placeholder until the ACME certificate
/// manager lands. Registered as <see cref="IProxyCertificateManager"/> so the provider's lifecycle is
/// complete and observable now; the registration is replaced, not the callers.
/// </summary>
public sealed class NoOpProxyCertificateManager(ILogger<NoOpProxyCertificateManager> logger)
    : IProxyCertificateManager {
    public void SetDesiredHosts(IReadOnlyCollection<string> hosts) =>
        logger.LogDebug(
            "Certificate management is not wired up yet; ignoring {Count} desired TLS host(s).", hosts.Count);

    public Task ForgetHostAsync(string host, CancellationToken ct) {
        logger.LogDebug("Certificate management is not wired up yet; nothing held for {Host} to forget.", host);
        return Task.CompletedTask;
    }
}
