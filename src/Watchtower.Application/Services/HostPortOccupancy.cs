using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services;

/// <summary>
/// Which containers on this host already publish a given host port — the reading behind the port-route
/// collision refusals (ADR-0033).
/// </summary>
/// <remarks>
/// A port route's listener is published on Watchtower's <em>own</em> container, so a stack that publishes
/// the same host port takes it away: whichever container the daemon starts second fails with "port is
/// already allocated". Asking first turns that into a sentence naming the container instead of a recreate
/// that rolls back, or a stack service that will not come up.
/// <para>
/// <b>Fail-open by design, in both directions.</b> A Docker call that throws — no socket, a bare-process
/// install, a daemon that is briefly unreachable — refuses nothing, and neither does a self that cannot be
/// identified. This is a convenience against a footgun, not a security boundary: nothing here decides what
/// is served, being unable to ask the daemon must not be what stops an operator creating a route, and a
/// <em>false</em> refusal naming Watchtower's own container is worse than a missed one — it would tell an
/// operator who followed the documented manual path (<c>- "9001:9001"</c> on Watchtower's own container,
/// then create the route) that their own binding is in the way.
/// </para>
/// </remarks>
public sealed class HostPortOccupancy(DockerEngineClient docker, ILogger<HostPortOccupancy> logger) {
    private const string ComposeProjectLabel = "com.docker.compose.project";
    private const string ComposeServiceLabel = "com.docker.compose.service";

    private int _warnedAboutTheList;
    private int _warnedAboutSelf;

    /// <summary>
    /// The refusals for the ports of <paramref name="ports"/> that another container already publishes,
    /// keyed by port — empty when none of them is taken, and empty when the question could not be asked.
    /// </summary>
    /// <param name="selfContainerId">
    /// Watchtower's own container id, which is excluded: it is where the listener lives, and a port it
    /// already publishes is the state this whole feature is trying to reach. Pass it when the caller has
    /// already inspected itself; null resolves it here, <c>HOSTNAME</c> → inspect → the authoritative long
    /// id, which is the reading <see cref="SelfUpdateService.DetectSelfAsync"/> starts from. Matching is
    /// exact id equality, never a prefix: <c>HOSTNAME</c> is only the short id on a container that kept
    /// Docker's default hostname, and one that carries a custom <c>hostname:</c> (which
    /// <see cref="ContainerCloneSpec"/> deliberately preserves) reads as a name no id begins with.
    /// </param>
    /// <remarks>
    /// Containers in <em>any</em> state count, the way <c>networks.ports</c> deliberately reads them: a
    /// stopped stack whose desired state is running comes back, taking the port with it. Only the TCP half
    /// counts — a <c>9001/udp</c> binding is not in the way of an HTTPS listener — and a type Docker left
    /// off is TCP, which is what the daemon assumes for a bare port number too.
    /// </remarks>
    public async ValueTask<IReadOnlyDictionary<int, string>> PublishedByOtherContainersAsync(
        IReadOnlyCollection<int> ports, string? selfContainerId, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(ports);
        if (ports.Count == 0) return ReadOnlyDictionary<int, string>.Empty;

        // Resolved before the list is fetched, because without it there is nothing to ask: every answer
        // would be one Watchtower cannot tell its own container from.
        var self = string.IsNullOrWhiteSpace(selfContainerId)
            ? await ResolveSelfAsync(ct)
            : selfContainerId;
        if (string.IsNullOrWhiteSpace(self)) return ReadOnlyDictionary<int, string>.Empty;

        var wanted = new HashSet<int>(ports);
        var blocked = new Dictionary<int, string>();
        try {
            // The projection is inside the try as well as the call. Every field it reads is one the
            // daemon can send as null despite the non-nullable model (see DockerContainerInfo), and
            // GetStatusAsync promises never to throw.
            foreach (var container in await docker.ListAllContainersAsync(ct)) {
                if (!string.Equals(container.Id, self, StringComparison.OrdinalIgnoreCase)) {
                    foreach (var port in container.Ports ?? []) {
                        if (port.PublicPort is not { } published || !wanted.Contains(published)) continue;
                        if (!IsTcpBinding(port.Type)) continue;
                        blocked.TryAdd(published, PortHeldBy(published, container));
                    }
                }
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            WarnOnce(ref _warnedAboutTheList, ex,
                "Could not read the container list from Docker, so the port-route host-port collision "
                + "check is skipped. A listen port another container publishes will not be refused.");
            return ReadOnlyDictionary<int, string>.Empty;
        }
        return blocked;
    }

    /// <summary>
    /// <inheritdoc cref="PublishedByOtherContainersAsync" path="/summary"/> The one-port form the route
    /// handlers use, over the same reading.
    /// </summary>
    public async ValueTask<string?> PublishedByAnotherContainerAsync(
        int listenPort, string? selfContainerId, CancellationToken ct) {
        var blocked = await PublishedByOtherContainersAsync([listenPort], selfContainerId, ct);
        return blocked.TryGetValue(listenPort, out var refusal) ? refusal : null;
    }

    /// <summary>
    /// This container's id, through the <c>HOSTNAME</c> → inspect the self-update uses, or null when
    /// Watchtower is not running as a container it can see. Null is a reason to refuse nothing at all —
    /// see the class remarks for why a false refusal is the worse failure.
    /// </summary>
    private async Task<string?> ResolveSelfAsync(CancellationToken ct) {
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(hostname)) {
            WarnOnce(ref _warnedAboutSelf, exception: null,
                "HOSTNAME is not set, so Watchtower cannot tell which container is its own; the "
                + "port-route host-port collision check is skipped.");
            return null;
        }

        try {
            var details = await docker.InspectContainerAsync(hostname, ct);
            return string.IsNullOrWhiteSpace(details.Id) ? null : details.Id;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            WarnOnce(ref _warnedAboutSelf, ex,
                "Could not identify Watchtower's own container from HOSTNAME, so the port-route "
                + "host-port collision check is skipped. A port Watchtower itself publishes must never "
                + "be reported as held by something else.");
            return null;
        }
    }

    /// <summary>
    /// One line per condition per process. Both conditions are steady states — a bare-process install has
    /// no socket and never will — so warning per call would be a line per route form an operator opens.
    /// </summary>
    private void WarnOnce(ref int latch, Exception? exception, string message) {
        if (Interlocked.Exchange(ref latch, 1) != 0) return;
        if (exception is null) logger.LogWarning(message);
        else logger.LogWarning(exception, message);
    }

    /// <summary>The refusal itself: what holds the port, and the two ways out of it.</summary>
    private static string PortHeldBy(int port, DockerContainerInfo container) {
        var names = container.Names ?? [];
        var name = names.Length > 0 && !string.IsNullOrWhiteSpace(names[0])
            ? names[0].TrimStart('/')
            : container.Id;
        var labels = container.Labels;
        var project = labels is not null
            && labels.TryGetValue(ComposeProjectLabel, out var p) && !string.IsNullOrWhiteSpace(p) ? p : null;
        var service = labels is not null
            && labels.TryGetValue(ComposeServiceLabel, out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
        var described = (project, service) switch {
            (not null, not null) => $" (stack {project}, service {service})",
            (not null, null) => $" (stack {project})",
            (null, not null) => $" (service {service})",
            _ => "",
        };
        return $"Host port {port} is already published by container {name}{described}. A port route needs "
            + "that port for Watchtower's own listener — remove that ports: entry from the stack or "
            + "choose another port.";
    }

    /// <summary>A binding with no protocol at all is TCP, the same way a bare port number is.</summary>
    private static bool IsTcpBinding(string? type) =>
        string.IsNullOrEmpty(type) || string.Equals(type, "tcp", StringComparison.OrdinalIgnoreCase);
}
