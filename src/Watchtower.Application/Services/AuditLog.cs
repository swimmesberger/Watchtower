using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Records <see cref="AuditEvent"/> rows — Watchtower's general "what did we change" trail, read by
/// <c>audit.listEvents</c>. Singleton (its callers are singletons), so every write opens a
/// short-lived scope (ADR-0004). Strictly best-effort: an audit failure is logged and swallowed,
/// because a bookkeeping problem must never break the operation it is describing.
/// </summary>
public class AuditLog(
    IServiceScopeFactory scopeFactory,
    TimeProvider time,
    ILogger<AuditLog> logger) {
    /// <summary>Newest rows kept across all categories; older ones are trimmed opportunistically on write.</summary>
    internal const int MaxRows = 2000;

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
                // External error messages are operator-facing but unbounded; cap so a pathological
                // response cannot bloat the audit table.
                Error = error is { Length: > 500 } ? error[..500] : error,
                CreatedAt = time.GetUtcNow(),
            });
            await db.SaveChangesAsync(ct);

            // Bounded retention: find the id that falls off the end and drop everything older.
            var threshold = await db.AuditEvents
                .OrderByDescending(x => x.Id)
                .Select(x => x.Id)
                .Skip(MaxRows - 1)
                .FirstOrDefaultAsync(ct);
            if (threshold > 0)
                await db.AuditEvents.Where(x => x.Id < threshold).ExecuteDeleteAsync(ct);
        } catch (Exception ex) {
            logger.LogWarning(ex, "Failed to record audit event {Category}/{Action} for {Target}.", category, action, target);
        }
    }
}
