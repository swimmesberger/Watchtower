using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Records <see cref="AuditEvent"/> rows — the instance's single audit trail, read by
/// <c>audit.listEvents</c> — for writers that have no transaction of their own: singletons and
/// background services (the proxy providers, the backup runs, the self-update). Every write opens a
/// short-lived scope (ADR-0004) and is strictly best-effort: an audit failure is logged and swallowed,
/// because a bookkeeping problem must never break the operation it is describing.
/// </summary>
/// <remarks>
/// Writers that DO have a transaction — the access-control plane's handlers and endpoints — add their
/// row to their own <see cref="WatchtowerDbContext"/> instead (see <see cref="AuthAudit"/>), so the
/// row commits with the act it records and a caller that hangs up cannot keep a denial out of the
/// trail. Both paths produce the same rows in the same table; only the commit discipline differs.
/// </remarks>
public class AuditLog(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<AuditLog> logger) {
    /// <summary>
    /// Newest rows kept <em>per category</em>; older ones are trimmed opportunistically on write. Per
    /// category rather than overall so a chatty category (logins) cannot evict a quiet one's history
    /// (a settings change made months ago is exactly the row someone comes looking for).
    /// </summary>
    internal const int MaxRowsPerCategory = 2000;

    /// <summary>Records one event. Virtual so tests can observe callers' auditing without a DB.</summary>
    public virtual async Task RecordAsync(
        string category, string action, string target, string? detail,
        bool success = true, string? error = null, string? actor = null, CancellationToken ct = default) {
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            db.AuditEvents.Add(new AuditEvent {
                Category = category,
                Action = action,
                Target = target,
                Detail = detail,
                Actor = actor,
                Success = success,
                Error = CapError(error),
                CreatedAt = time.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);
            await TrimAsync(db, ct);
        } catch (Exception ex) {
            logger.LogWarning(ex, "Failed to record audit event {Category}/{Action} for {Target}.", category, action, target);
        }
    }

    /// <summary>
    /// The display name for the acting user of a handler — what <see cref="AuditEvent.Actor"/> holds.
    /// Resolves the claim's user id to the account name; the implicit local administrator
    /// (<see cref="ImplicitAdminCurrentUser.LocalUserId"/>) is recorded as <c>local</c>; an anonymous
    /// or unknown caller as null, which the UI renders as <c>system</c>.
    /// </summary>
    public async Task<string?> ActorAsync(ICurrentUser currentUser, CancellationToken ct = default) {
        if (string.IsNullOrEmpty(currentUser.UserId)) return null;
        if (currentUser.UserId == ImplicitAdminCurrentUser.LocalUserId) return ImplicitAdminCurrentUser.LocalUserId;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await ResolveActorAsync(db, currentUser.UserId, ct);
    }

    /// <summary>
    /// Same resolution as <see cref="ActorAsync"/>, against a caller's own context — for writers that
    /// commit the row inside their transaction. Falls back to the raw id when the account is gone, so
    /// the row still says who, just less readably.
    /// </summary>
    public static async Task<string?> ResolveActorAsync(WatchtowerDbContext db, string? userId, CancellationToken ct = default) {
        if (string.IsNullOrEmpty(userId)) return null;
        if (userId == ImplicitAdminCurrentUser.LocalUserId) return userId;
        if (!int.TryParse(userId, out var id)) return userId;
        var name = await db.Users.AsNoTracking()
            .Where(u => u.Id == id)
            .Select(u => u.UserName)
            .FirstOrDefaultAsync(ct);
        return name ?? userId;
    }

    /// <summary>
    /// External error messages are operator-facing but unbounded; capped so a pathological response
    /// cannot bloat the audit table.
    /// </summary>
    internal static string? CapError(string? error) => error is { Length: > 500 } ? error[..500] : error;

    /// <summary>
    /// Bounded retention: every category over the cap loses its oldest rows. Runs on the best-effort
    /// path only — the transactional writers never pay for it — but trims every category, so the rows
    /// they write are bounded too.
    /// </summary>
    private static async Task TrimAsync(WatchtowerDbContext db, CancellationToken ct) {
        var over = await db.AuditEvents
            .GroupBy(x => x.Category)
            .Where(g => g.Count() > MaxRowsPerCategory)
            .Select(g => g.Key)
            .ToListAsync(ct);
        foreach (var category in over) {
            var threshold = await db.AuditEvents
                .Where(x => x.Category == category)
                .OrderByDescending(x => x.Id)
                .Select(x => x.Id)
                .Skip(MaxRowsPerCategory - 1)
                .FirstOrDefaultAsync(ct);
            if (threshold > 0)
                await db.AuditEvents.Where(x => x.Category == category && x.Id < threshold).ExecuteDeleteAsync(ct);
        }
    }
}
