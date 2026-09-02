namespace Watchtower.Application.Services;

/// <summary>
/// In-flight state of a host-port publish, persisted as typed JSON under the Global-scope settings key
/// <c>proxy.ports.runtime</c>.
/// </summary>
/// <remarks>
/// Separate from <c>self.runtime</c> rather than folded into it (ADR-0033). The two coordinators are the
/// same machinery but not the same operation: a self-update's stage drives the System page's
/// "pulling…/restarting…" banner and its error is about an image, and a port publish landing in that
/// record would both mislabel itself and overwrite a self-update that is genuinely in flight.
/// </remarks>
public sealed record SelfPortPublishRuntime {
    /// <summary>Apply stage as a lowercase string: "idle", "restarting" or "error".</summary>
    public string ApplyStage { get; init; } = "idle";

    /// <summary>Why the last apply failed, or null. Set only by a recreate that failed and rolled back.</summary>
    public string? ApplyError { get; init; }

    /// <summary>The coordinator container to reconcile at the next startup, or null when there is none.</summary>
    public string? CoordinatorId { get; init; }
}

/// <summary>
/// What a host-port apply would do: the ports to publish, the ports to stop publishing, and the managed
/// set that follows from carrying it out.
/// </summary>
/// <param name="Publish">Ports a port route wants that the container does not currently bind.</param>
/// <param name="Unpublish">
/// Ports Watchtower published earlier, still bound, that no port route wants any more. Never contains a
/// port Watchtower did not publish itself.
/// </param>
/// <param name="NextManaged">
/// The managed set after the recreate: the claims that survive it, plus the ports being published. A
/// port the operator already publishes is deliberately absent — the route it satisfies is served either
/// way, and adopting it would let a later route deletion take away a binding Watchtower never made.
/// </param>
public sealed record PortBindingPlan(
    IReadOnlyList<int> Publish, IReadOnlyList<int> Unpublish, IReadOnlyList<int> NextManaged) {
    /// <summary>Nothing to do — the container already publishes exactly what the routes need.</summary>
    public bool IsNoOp => Publish.Count == 0 && Unpublish.Count == 0;

    /// <summary>
    /// What the claim says while the recreate is in flight: <see cref="NextManaged"/> <em>plus</em> the
    /// ports being released.
    /// </summary>
    /// <remarks>
    /// The claim has to be written before the coordinator is spawned, because the coordinator ends this
    /// process and there is no "after" — so it is written against an outcome that has not happened yet.
    /// Writing <see cref="NextManaged"/> alone would be right only if the recreate always succeeded: a
    /// rollback (the new port is held by another process, so the create or start fails and the old
    /// container is restarted still binding it) would leave the port bound with the claim already gone,
    /// and since the startup reconcile only ever <em>prunes</em> claims, nothing could ever adopt it
    /// again — a bound, unmanaged port with no in-app way to release it.
    /// <para>
    /// Keeping the released ports in the claim is safe in the direction that matters: a claim is only
    /// ever acted on for a port that is also currently bound, so once the release really lands the
    /// startup prune (<c>managed ∩ bound</c>) drops it on its own.
    /// </para>
    /// </remarks>
    public IReadOnlyList<int> ClaimedThroughTheRecreate =>
        [.. new SortedSet<int>(NextManaged.Concat(Unpublish))];
}

/// <summary>One port route's listen port, as the Watchtower container currently publishes it (or not).</summary>
/// <param name="Bound">Whether the container publishes this host port right now.</param>
/// <param name="Managed">
/// Whether Watchtower published it itself — and therefore whether it would take it away again when the
/// route goes. False for a port the operator declared, which Watchtower never removes.
/// </param>
/// <param name="BlockedBy">
/// Why this port cannot be published, naming the other container that already holds it, or null when
/// nothing does. Only ever set for a port that is <em>not</em> <paramref name="Bound"/>: an apply that
/// tried it would recreate this container, fail to start and roll back, so this is the difference
/// between "not published yet" and "not publishable until something else lets go".
/// </param>
public sealed record HostPortBinding(
    int Port, int RouteId, string ServiceName, bool Bound, bool Managed, string? BlockedBy);

/// <summary>
/// How the port routes' host ports stand on this instance's own container: what the Routes page needs to
/// mark a row "not published" and to decide whether it may offer the publish button at all.
/// </summary>
/// <param name="ContainerDetected">
/// Whether Watchtower could inspect its own container. False outside Docker (or when the daemon cannot
/// be reached), where <paramref name="Ports"/> report nothing and publishing is impossible.
/// </param>
/// <param name="UnavailableReason">
/// Why an apply would be refused, or null when it would be accepted. Rendered as the disabled button's
/// tooltip, so it has to be a sentence an operator can act on.
/// </param>
/// <param name="LastError">The error a previous apply left behind, or null.</param>
/// <param name="PendingUnpublish">
/// Ports Watchtower published that no port route wants any more — what an apply would release. The one
/// thing the per-route rows cannot express, since a port left over from a deleted route has no row.
/// </param>
public sealed record SelfPortPublishStatus(
    bool ContainerDetected,
    string? UnavailableReason,
    string? LastError,
    IReadOnlyList<HostPortBinding> Ports,
    IReadOnlyList<int> PendingUnpublish);
