using System.Globalization;
using Watchtower.Application.Modules.Notifications.Contracts;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Notifications.Services;

/// <summary>
/// Whether a failed deploy is worth a push notification, and what it says (ADR-0041). Pure functions over
/// the facts the notifier loads, so the spam guard is testable without a database.
/// </summary>
internal static class DeployFailureNotificationPolicy {
    /// <summary>
    /// The spam guard. A deploy someone explicitly asked for (manual, webhook, release-manual,
    /// volume-recreate) always notifies — each is its own question and wants its own answer. An
    /// automatic one (<see cref="DeployTriggers.IsAutomatic"/>) notifies only on the <em>transition</em>
    /// into failing: when the stack's previous finished deploy did not fail too. The daily window or the
    /// release reconcile would otherwise page on every repetition of the same unfixed failure.
    /// </summary>
    /// <param name="triggeredBy">The failed deploy's trigger.</param>
    /// <param name="previousTerminalStatus">
    /// The status of the stack's most recent finished (<c>success</c>/<c>failed</c>) deploy before this
    /// one, or null when there was none — a first deploy that fails is news.
    /// </param>
    public static bool ShouldNotify(string triggeredBy, string? previousTerminalStatus) =>
        !DeployTriggers.IsAutomatic(triggeredBy)
        || !string.Equals(previousTerminalStatus, "failed", StringComparison.Ordinal);

    /// <summary>
    /// The notification for a failed deploy. Deliberately carries no deploy output — it can contain
    /// secrets, and the payload is handed to a third-party push service — only where to read it.
    /// </summary>
    public static PushNotification Build(int stackId, string stackName, int deployEventId, string triggeredBy) =>
        new(
            Title: $"Deploy failed: {stackName}",
            Body: $"Triggered by {triggeredBy} · tap to view the log",
            Url: string.Create(CultureInfo.InvariantCulture, $"/stacks/{stackId}?event={deployEventId}"),
            // One per stack: a newer failure of the same stack replaces the older notification.
            Tag: string.Create(CultureInfo.InvariantCulture, $"deploy-{stackId}"));
}
