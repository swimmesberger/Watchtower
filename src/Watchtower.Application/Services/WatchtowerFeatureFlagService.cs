using Elarion.Abstractions.Features;
using Elarion.Abstractions.Identity;

namespace Watchtower.Application.Services;

/// <summary>
/// Resolves Watchtower's client-exposed feature flags (ADR-0030) from the deployment and the caller rather
/// than from a flag store. Unknown names fail closed.
/// </summary>
/// <remarks>
/// One implementation for every flag because the session bootstrap injects a single
/// <see cref="IFeatureFlagService"/>: a second registration would not compose, it would replace. The two
/// names it answers are of different kinds, which is worth stating rather than blurring:
/// <list type="bullet">
///   <item><description>
///     <c>metrics-history</c> is <b>deployment-scoped</b> — true exactly when the active
///     <see cref="IMetricsSource"/> backend can answer historical time ranges (the InfluxDB backend,
///     ADR-0007). Boot-fixed by DI; runtime-variable availability (e.g. Docker unreachable) stays on the
///     data as <c>available</c>/<c>reason</c>.
///   </description></item>
///   <item><description>
///     <c>apps-portal</c> is <b>per-caller</b> — see <see cref="AppsPortalFlag"/>. ADR-0030's flags are
///     evaluated against the current user, so this is the seam the snapshot already had for a fact about
///     who is asking; nothing new is added to the wire contract to carry it.
///   </description></item>
/// </list>
/// Scoped, not singleton: <see cref="ICurrentUser"/> is the request's identity snapshot.
/// </remarks>
public sealed class WatchtowerFeatureFlagService(IMetricsSource metrics, ICurrentUser currentUser)
    : IFeatureFlagService {
    /// <summary>The flag name exposed on the Metrics module's <c>[ClientFeatures]</c>.</summary>
    public const string HistoryFlag = "metrics-history";

    /// <summary>
    /// The flag name exposed on the Proxy module's <c>[ClientFeatures]</c>: true when the caller should be
    /// shown the applications portal instead of the management UI — i.e. they are signed in and their
    /// account belongs to a realm other than the operator one (docs/central-auth/design.md §13).
    /// </summary>
    /// <remarks>
    /// Stated in the affirmative — "show the portal" — rather than as "is an operator", because that is the
    /// polarity that degrades safely. Every way this can fail to be answered (the Proxy module disabled, an
    /// older snapshot, an unauthenticated boot) leaves it <see langword="false"/>, which is the management
    /// UI exactly as it renders today. The inverse would answer a disabled module by hiding the management
    /// UI from the operator.
    /// <para>
    /// A read-only UX projection, like everything else in the snapshot: what actually refuses a realm
    /// account the management surface is <c>SystemRealmAuthorizer</c>, on every transport.
    /// </para>
    /// </remarks>
    public const string AppsPortalFlag = "apps-portal";

    /// <inheritdoc />
    public ValueTask<bool> IsEnabledAsync(string feature, CancellationToken ct = default) =>
        ValueTask.FromResult(feature switch {
            HistoryFlag => metrics.Capabilities.HistoryAvailable,
            AppsPortalFlag => currentUser.IsAuthenticated && !WatchtowerClaims.IsSystemRealm(currentUser),
            _ => false,
        });
}
