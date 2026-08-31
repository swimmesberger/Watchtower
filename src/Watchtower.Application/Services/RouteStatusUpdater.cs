using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Writes certificate outcomes onto the <c>routes</c> rows for the in-process proxy, the way
/// <see cref="CloudflareTunnelProvider"/> does for its reconcile. A singleton over
/// <see cref="IServiceScopeFactory"/> because its callers are singletons and background workers with
/// no ambient scope, and it writes with <c>ExecuteUpdateAsync</c> so it never fights the DbContext a
/// request may be holding on the same row.
/// </summary>
/// <remarks>
/// Bookkeeping, not the record: the audit trail and the certificate store are authoritative, the
/// route status is what the Routes page shows. So nothing here throws — a failed status write is
/// logged and the certificate work it was reporting on carries on.
/// </remarks>
public sealed class RouteStatusUpdater(IServiceScopeFactory scopeFactory, ILogger<RouteStatusUpdater> logger) {
    /// <summary>Status details are a UI convenience; a CA error page pasted in whole is not.</summary>
    private const int MaxDetailLength = 500;

    /// <summary>Records a successful issuance: the domain is served, with the certificate's expiry.</summary>
    public Task RecordIssuedAsync(string domain, DateTimeOffset notAfter, CancellationToken ct) =>
        UpdateAsync(
            domain,
            "issued",
            rows => rows.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RouteStatus.Active)
                .SetProperty(r => r.StatusDetail, (string?)null)
                .SetProperty(r => r.CertNotAfter, (DateTimeOffset?)notAfter), ct));

    /// <summary>
    /// Records a failed issuance. <paramref name="status"/> separates "this will not work until you
    /// point DNS here" (<see cref="RouteStatus.AwaitingDns"/>) from "the CA refused"
    /// (<see cref="RouteStatus.Error"/>) — the two need different things from the operator.
    /// <see cref="Route.CertNotAfter"/> is left alone: a renewal that fails does not un-issue the
    /// certificate still being served.
    /// </summary>
    public Task RecordFailedAsync(string domain, RouteStatus status, string detail, CancellationToken ct) {
        if (status is not (RouteStatus.Error or RouteStatus.AwaitingDns))
            throw new ArgumentOutOfRangeException(
                nameof(status), status, "A failure is recorded as either Error or AwaitingDns.");
        var capped = detail.Length > MaxDetailLength ? detail[..MaxDetailLength] : detail;
        return UpdateAsync(
            domain,
            "failed",
            rows => rows.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.StatusDetail, capped), ct));
    }

    /// <summary>
    /// Records the outcome for port-bound routes (ADR-0033), which are addressed by row rather than by
    /// hostname — so the only thing that can identify them here is their id.
    /// </summary>
    /// <remarks>
    /// One write for the whole set, because their outcome <em>is</em> one outcome: a single LAN
    /// certificate covers every port route at once, so they cannot succeed and fail separately. A blank
    /// <paramref name="detail"/> with a <paramref name="notAfter"/> is the success form.
    /// </remarks>
    /// <param name="routeIds">The rows to write. An empty set is a no-op, not an empty <c>IN ()</c>.</param>
    /// <param name="status"><see cref="RouteStatus.Active"/> or <see cref="RouteStatus.Error"/>.</param>
    /// <param name="detail">The reason, for an error; null clears whatever was there.</param>
    /// <param name="notAfter">The certificate's expiry on success; null leaves the column alone.</param>
    public async Task RecordPortRoutesAsync(
        IEnumerable<int> routeIds,
        RouteStatus status,
        string? detail,
        DateTimeOffset? notAfter,
        CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(routeIds);
        if (status is not (RouteStatus.Active or RouteStatus.Error))
            throw new ArgumentOutOfRangeException(
                nameof(status), status,
                "A port route settles at Active or Error; it never waits for DNS, having no domain.");
        var ids = routeIds.Distinct().ToList();
        if (ids.Count == 0) return;
        var capped = detail is { Length: > MaxDetailLength } ? detail[..MaxDetailLength] : detail;

        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            // Two shapes rather than one with a conditional: on a failure the expiry column is left
            // exactly as it was, because a renewal that failed does not un-issue the certificate still
            // being served — the same rule RecordFailedAsync states for domain routes.
            //
            // Each carries "unless it already says this". The internal certificate pass runs on a
            // five-minute cadence on every instance and settles the same rows every time; rewriting them
            // would churn the concurrency token under whatever the operator is editing on the Routes page,
            // for a value that did not move.
            if (notAfter is { } expiry) {
                await db.Routes
                    .Where(r => ids.Contains(r.Id)
                        && (r.Status != status || r.StatusDetail != capped || r.CertNotAfter != expiry))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, status)
                        .SetProperty(r => r.StatusDetail, capped)
                        .SetProperty(r => r.CertNotAfter, (DateTimeOffset?)expiry), ct);
            } else {
                await db.Routes
                    .Where(r => ids.Contains(r.Id) && (r.Status != status || r.StatusDetail != capped))
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, status)
                        .SetProperty(r => r.StatusDetail, capped), ct);
            }
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogDebug(ex, "Failed to record the {Status} status for {Count} port route(s).", status, ids.Count);
        }
    }

    /// <summary>
    /// Marks freshly created routes as waiting for a certificate, so a new route does not sit at a
    /// bare "Pending" with no explanation until the order completes.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: only rows that are already <see cref="RouteStatus.Pending"/> <em>and</em>
    /// carry no detail are touched. A route that is Active must not be knocked back to Pending by a
    /// reconcile that had nothing to do with it — the certificate it is serving is still valid — and a
    /// Pending row that already has a detail is one a previous attempt wrote something about, which is
    /// more informative than this generic line.
    /// </remarks>
    public async Task MarkPendingAsync(IEnumerable<string> domains, CancellationToken ct) {
        var wanted = domains
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (wanted.Count == 0) return;
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.Routes
                .Where(r => r.Domain != null && wanted.Contains(r.Domain)
                    && r.Status == RouteStatus.Pending && r.StatusDetail == null)
                // Only the detail: the filter already pins these rows to Pending.
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.StatusDetail, "Waiting for a certificate"), ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogDebug(ex, "Failed to mark {Count} route(s) as waiting for a certificate.", wanted.Count);
        }
    }

    private async Task UpdateAsync(
        string domain, string what, Func<IQueryable<Route>, Task<int>> update) {
        var host = domain.Trim().ToLowerInvariant();
        if (host.Length == 0) return;
        try {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await update(db.Routes.Where(r => r.Domain == host));
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            logger.LogDebug(ex, "Failed to record the {What} certificate status for {Domain}.", what, host);
        }
    }
}
