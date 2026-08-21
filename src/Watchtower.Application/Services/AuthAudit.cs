using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// The access-control plane's way into the audit trail: queues an <see cref="AuditEvent"/> on the
/// caller's own <see cref="WatchtowerDbContext"/>, so the row commits in the same
/// <c>SaveChangesAsync</c> as the act it records — a login that succeeded, a denial that was answered,
/// a policy that changed. The caller decides when to commit (and, for denials and failures, does so on
/// <see cref="CancellationToken.None"/>: a caller that hangs up must not keep its row out of the trail).
/// </summary>
/// <remarks>
/// The row is reference-free like every other audit row — the actor and the target are recorded by
/// name, resolved here from the ids the plane works with, so the trail outlives the accounts and apps
/// it mentions. Rejections record as <c>Success = false</c>, which is what the Audit page tones.
/// </remarks>
public static class AuthAudit {
    /// <summary>Bounds an attacker-controlled name (a failed login's posted user name) before it lands in the trail.</summary>
    private const int MaxSubjectLength = 100;

    /// <summary>
    /// Queues a row for <paramref name="kind"/> (see <see cref="AuthEventKinds"/>). <paramref name="userId"/>
    /// resolves to the actor's name; <paramref name="routeId"/> to the target app's domain. An explicit
    /// <paramref name="target"/> wins over the route, and a row with neither names the actor as its
    /// target — a login is about the account.
    /// </summary>
    public static async Task<AuditEvent> QueueAsync(
        WatchtowerDbContext db,
        TimeProvider time,
        string kind,
        int? userId,
        int? routeId,
        string? detail,
        bool success = true,
        string? target = null,
        string? actor = null,
        CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(time);

        actor ??= userId is { } uid
            ? await db.Users.AsNoTracking().Where(u => u.Id == uid).Select(u => u.UserName).FirstOrDefaultAsync(ct)
            : null;
        target ??= routeId is { } rid
            ? await db.Routes.AsNoTracking().Where(r => r.Id == rid).Select(r => r.Domain).FirstOrDefaultAsync(ct)
            : null;

        var row = new AuditEvent {
            Category = AuthEventKinds.CategoryOf(kind),
            Action = kind,
            Target = Bound(target ?? actor ?? ""),
            Detail = detail,
            Actor = actor is null ? null : Bound(actor),
            Success = success,
            CreatedAt = time.GetUtcNow(),
        };
        db.AuditEvents.Add(row);
        return row;
    }

    private static string Bound(string value) =>
        value.Length > MaxSubjectLength ? value[..MaxSubjectLength] : value;
}
