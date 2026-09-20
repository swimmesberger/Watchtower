namespace Watchtower.Application.Services;

/// <summary>
/// The values <see cref="Entities.DeployEvent.TriggeredBy"/> takes, and the one rule that reads them.
/// </summary>
/// <remarks>
/// Constants rather than an enum because the column is free text and has been since before ADR-0026:
/// making it an enum now would turn every historical row written by a path that has since been renamed
/// into a migration problem, for no gain. What matters is that the two release triggers are spelled the
/// same way everywhere, because <see cref="MayShortCircuit"/> keys on them.
/// </remarks>
public static class DeployTriggers {
    /// <summary>An operator pressed Deploy.</summary>
    public const string Manual = "manual";

    /// <summary>The stack's own deploy webhook was called.</summary>
    public const string Webhook = "webhook";

    /// <summary>The volume-recreate data-wipe flow.</summary>
    public const string VolumeRecreate = "volume-recreate";

    /// <summary>Pull-based on-change polling found something new (<c>Git</c> mode only).</summary>
    public const string AutoUpdate = "auto-update";

    /// <summary>The daily auto-deploy window.</summary>
    public const string Schedule = "schedule";

    /// <summary>
    /// A release arrived and <see cref="ReleaseRolloutService"/> fanned it out to this stack. One of
    /// the two triggers a deploy may short-circuit on — see <see cref="MayShortCircuit"/>.
    /// </summary>
    public const string Release = "release";

    /// <summary>
    /// An operator pinned, unpinned, or rolled a release out by hand (<c>stacks.setRelease</c>,
    /// <c>products.deployRelease</c>). Deliberately <em>not</em> <see cref="Release"/>: an explicit
    /// action must run the full pipeline even when the resolved release does not change.
    /// </summary>
    public const string ReleaseManual = "release-manual";

    /// <summary>
    /// <see cref="AutoDeployBackgroundService"/>'s periodic reconcile found a release that the
    /// <see cref="Release"/> fan-out never delivered — the safety net for a lost enqueue (design.md
    /// §"Auto-deploy precedence").
    /// </summary>
    /// <remarks>
    /// Spelled apart from <see cref="Release"/> although it asks for exactly the same thing, because
    /// the two answer different questions in the deploy history: "a release went out" versus "the push
    /// path missed one". Collapsing them would hide the only evidence that webhook delivery is
    /// unreliable for an install — which is the whole reason the reconcile exists.
    /// </remarks>
    public const string ReleaseReconcile = "release-reconcile";

    /// <summary>
    /// Whether a deploy with this trigger may complete as a no-op when the stack is already on the
    /// resolved release.
    /// </summary>
    /// <remarks>
    /// True for the two convergent release triggers, <see cref="Release"/> and
    /// <see cref="ReleaseReconcile"/>. A fan-out asks every latest-tracking stack of a product to
    /// converge onto the newest release, and a stack that already converged — because its own deploy
    /// resolved the same release moments earlier — has nothing left to do; skipping it is what keeps a
    /// 200-tenant rollout from re-deploying the tenants that were quick. The reconcile asks the
    /// identical question a tick later and so wants the identical answer: when it races a fan-out it
    /// had already written off, "already there" is both the correct outcome and the cheap one. Every
    /// other trigger runs the
    /// pipeline regardless, because a deploy converges more than the release: compose file, environment
    /// variables, the generated override and the reverse-proxy wiring all travel with it, and a manual
    /// or webhook deploy that silently did nothing would be a bug report.
    /// </remarks>
    public static bool MayShortCircuit(string triggeredBy) =>
        string.Equals(triggeredBy, Release, StringComparison.Ordinal)
        || string.Equals(triggeredBy, ReleaseReconcile, StringComparison.Ordinal);
}
