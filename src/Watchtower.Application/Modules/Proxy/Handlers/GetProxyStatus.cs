using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>Reports whether the reverse proxy is enabled and running, which provider serves it, and the route count.</summary>
[Handler("proxy.getStatus")]
public sealed class GetProxyStatus(
    WatchtowerDbContext db,
    IProxyProvider proxy,
    IOptionsMonitor<WatchtowerOptions> options,
    YarpListenerState listener,
    BoundListenerPorts bound,
    CertificateManager certificates)
    : IHandler<GetProxyStatus.Query, Result<GetProxyStatus.Response>> {
    public sealed record Query;

    /// <param name="CaddyRunning">
    /// Whether the active provider's data plane reports running. Historical name kept for the wire
    /// contract; with the cloudflare provider it reflects the cloudflared container (or "configured"
    /// in fully-unmanaged mode), and with the in-process provider whether the TLS ingress listener is
    /// configured — which, since the listeners follow the settings, is the same as it being up.
    /// </param>
    /// <param name="ProviderDetail">
    /// A provider-specific caveat worth surfacing next to the status, or null when there is nothing to
    /// say. It exists for the states that are otherwise invisible: the in-process proxy running with no
    /// TLS ingress, where routes resolve and are served — just over plain HTTP — and with no plain-HTTP
    /// ingress, where no certificate can be issued over HTTP-01.
    /// </param>
    public sealed record Response(
        bool Enabled, bool CaddyRunning, int RouteCount, string Provider, string? ProviderDetail);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var count = await db.Routes.CountAsync(ct);
        var running = await proxy.IsRunningAsync(ct);
        var proxyOptions = options.CurrentValue.Proxy;
        var yarp = proxyOptions.ResolveProvider() == ProxyProviderKind.Yarp && proxy.Enabled;
        return new Response(
            proxy.Enabled, running, count, proxyOptions.ProviderName(),
            yarp ? Detail(proxyOptions.Yarp) : null);
    }

    /// <summary>
    /// The caveats worth a sentence next to the status, joined. The "off" cases are configuration rather
    /// than failure — an operator who set a port to 0 meant it — so they are stated as what they are and
    /// not as the old "never came up" alarm; a port that was asked for and did not bind is the one real
    /// alarm here, and it is additive because turning one listener off does not stop the other from
    /// failing.
    /// </summary>
    private string? Detail(YarpProxyOptions yarp) {
        var notes = new List<string>();

        if (!listener.HttpsBound)
            notes.Add(yarp.HttpsPort == 0
                ? "HTTPS ingress disabled (port 0) — routes are served over plain HTTP only"
                : "HTTPS ingress is not configured — routes are served over plain HTTP only");
        if (yarp.HttpPort == 0)
            notes.Add("HTTP ingress disabled (port 0) — ACME HTTP-01 validation cannot reach this instance");
        else if (listener.ManagementPort is not null && !listener.IngressPorts.Contains(yarp.HttpPort))
            // A port was asked for and the projection did not produce a listener for it — today that means
            // it collided with the management port. Otherwise the only trace is one warning in the log.
            // Gated on the management port being known, which is what says the listener state was derived
            // from a real projection at all: the unit-test hosts have no host wiring to derive it.
            notes.Add(
                $"HTTP ingress port {yarp.HttpPort} was refused (see the logs) — "
                + "ACME HTTP-01 validation cannot reach this instance");

        notes.AddRange(FailedBinds());

        // Only while there is something outstanding: "12 of 12 issued" is noise next to a status that
        // already says the proxy is running — and next to a caveat that says something is wrong.
        if (notes.Count == 0 && CertificateProgress() is { } progress) notes.Add(progress);

        return notes.Count == 0 ? null : string.Join(" · ", notes);
    }

    /// <summary>
    /// Ports the configuration asks for that the server is not listening on. Diagnostics only — the
    /// dispatcher keeps acting on the configured set, which is the safe direction to be wrong in.
    /// </summary>
    /// <remarks>
    /// Reachable in exactly one way. A bind failure at startup is fatal (Kestrel rethrows out of
    /// <c>StartAsync</c>), so a <em>running</em> instance that disagrees with its own configuration got
    /// there by an ingress port being moved at runtime onto one something else holds: Kestrel logs that at
    /// Critical, keeps the listeners it already had and carries on, so the only other symptom is traffic
    /// never arriving on a port the Settings page cheerfully reports as configured. Best effort — where
    /// the server exposes no addresses (the unit-test hosts, <c>TestServer</c>) nothing is claimed.
    /// </remarks>
    private IEnumerable<string> FailedBinds() {
        if (bound.Current is not { } boundPorts) return [];
        return listener.IngressPorts
            .Where(port => !boundPorts.Contains(port))
            .Order()
            .Select(port => $"ingress port {port} failed to bind — see the logs");
    }

    /// <summary>
    /// How far through issuance the proxy is, or null when there is nothing outstanding. Counted off the
    /// manager's own view rather than the route rows, so a login host waiting for its certificate is
    /// included — it has no route row and would otherwise be invisible here.
    /// </summary>
    private string? CertificateProgress() {
        var desired = certificates.Snapshot().Where(s => s.Desired).ToArray();
        if (desired.Length == 0) return null;
        var active = desired.Count(s => s.State == "active");
        return active == desired.Length ? null : $"{active} of {desired.Length} certificates issued";
    }
}
