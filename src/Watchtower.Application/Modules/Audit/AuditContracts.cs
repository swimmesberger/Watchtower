using System.Linq.Expressions;
using Watchtower.Application.Entities;

namespace Watchtower.Application.Modules.Audit;

/// <summary>
/// The public projection of an <see cref="AuthEvent"/>. Carries the names of the subjects the row
/// mentions alongside their ids so the view needs no second round trip to render a row, and nothing
/// beyond what the row itself holds — the trail is read by administrators, but a projection that cannot
/// carry a secret cannot leak one in a future refactor.
/// </summary>
/// <param name="UserName">
/// The account's login name, or <see langword="null"/> when the row names no account, or names one that
/// has since been deleted. Both foreign keys are <c>SET NULL</c> on delete (the trail outlives its
/// subjects), so a row about a deleted account keeps its <see cref="AuthEvent.Detail"/> — which is where
/// the writers put the name for exactly this reason — and loses the reference.
/// </param>
/// <param name="RouteDomain">
/// The app's domain, or <see langword="null"/> on the same terms as <paramref name="UserName"/>.
/// </param>
public sealed record AuthEventDto(
    int Id,
    string Kind,
    int? UserId,
    string? UserName,
    int? RouteId,
    string? RouteDomain,
    string? Detail,
    DateTimeOffset CreatedAt);

/// <summary>
/// The projection and the paging arithmetic the Audit module's readers share.
/// </summary>
public static class AuditMapping {
    /// <summary>Rows returned when the caller names no limit — one screenful of "load more".</summary>
    public const int DefaultLimit = 100;

    /// <summary>
    /// The most rows one call may return. A ceiling rather than a refusal: the trail is append-only and
    /// unbounded, so an over-large limit is a client that has not been taught to page, not a caller doing
    /// something wrong — clamping keeps it working and keeps the response bounded.
    /// </summary>
    public const int MaxLimit = 500;

    /// <summary>Normalizes a requested page size into the accepted range.</summary>
    public static int ClampLimit(int? limit) =>
        limit is not { } value || value <= 0 ? DefaultLimit : Math.Min(value, MaxLimit);

    /// <summary>
    /// Projects a row for the API, resolving both optional references null-safely.
    /// </summary>
    /// <remarks>
    /// An expression rather than a method so the whole page is one <c>SELECT</c> with two left joins:
    /// the navigations are optional in the schema, so <c>e.User.UserName</c> would be a null dereference
    /// the moment a subject is deleted, which is the normal end state of a long-lived trail rather than
    /// an edge case.
    /// </remarks>
    public static readonly Expression<Func<AuthEvent, AuthEventDto>> Projection = e => new AuthEventDto(
        e.Id,
        e.Kind,
        e.UserId,
        e.User != null ? e.User.UserName : null,
        e.RouteId,
        e.Route != null ? e.Route.Domain : null,
        e.Detail,
        e.CreatedAt);
}
