using Elarion.Abstractions.Identity;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Publishes the port routes' listen ports on Watchtower's own container, by recreating it (ADR-0033).
/// </summary>
/// <remarks>
/// This restarts Watchtower — the management plane included — so it is never automatic: the UI asks for
/// an explicit confirmation and this handler is what that confirmation calls. It answers before the
/// restart lands, which the coordinator's three-second delay exists for; the caller sees the response,
/// then the connection drops for a few seconds while the container comes back with the new ports.
/// <para>
/// A no-op is a success, not a refusal: the container already publishes what the routes need, which is
/// the state the button is asking for.
/// </para>
/// </remarks>
[Handler("proxy.applyPortBindings")]
public sealed class ApplyPortBindings(SelfPortPublishService ports, AuditLog audit, ICurrentUser currentUser)
    : IHandler<ApplyPortBindings.Command, Result<ApplyPortBindings.Response>> {
    public sealed record Command;

    /// <param name="Restarting">
    /// Whether a recreate was actually started. False when there was nothing to change — the caller
    /// should say so rather than warning about a restart that is not coming.
    /// </param>
    public sealed record Response(
        bool Restarting,
        IReadOnlyList<int> Published,
        IReadOnlyList<int> Unpublished,
        string Message);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        try {
            var plan = await ports.ApplyAsync(actor: await audit.ActorAsync(currentUser, ct), ct);
            return plan.IsNoOp
                ? new Response(false, [], [], "Every port route's host port is already published — nothing to apply.")
                : new Response(true, plan.Publish, plan.Unpublish, Describe(plan));
        } catch (InvalidOperationException ex) {
            // The service's refusals are operator-facing sentences (not in a container, another instance
            // is running, an apply is already in flight), so they are surfaced as they are.
            return AppError.Validation(ex.Message);
        }
    }

    private static string Describe(PortBindingPlan plan) {
        var parts = new List<string>();
        if (plan.Publish.Count > 0) parts.Add($"publishing {string.Join(", ", plan.Publish)}");
        if (plan.Unpublish.Count > 0) parts.Add($"releasing {string.Join(", ", plan.Unpublish)}");
        return $"Watchtower is restarting — {string.Join(" and ", parts)}.";
    }
}
