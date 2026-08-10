using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// One tenant stack the proven user may reach. The <see cref="StackId"/> never leaves the host: it is only
/// how a caller recognises its own row (<c>current</c>), so the wire DTOs carry slug and domain alone.
/// </summary>
/// <param name="StackId">Watchtower's numeric id for the tenant's stack.</param>
/// <param name="Slug">Tenant identifier within the template.</param>
/// <param name="Domain">Primary domain the tenant is served on.</param>
public sealed record AccessibleTenant(int StackId, string Slug, string Domain);

/// <summary>
/// User-scoped tenant discovery: which sibling tenants of a template the visitor currently standing in
/// front of a stack may switch to. Backs <c>GET /api/app/tenants/accessible</c> and
/// <c>GET /api/mgmt/templates/{id}/tenants/accessible</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place a stack learns anything about its siblings, and it is a scoped amendment of
/// ADR-0008's invariant rather than a hole in it: a stack still cannot enumerate the estate on its own, it
/// can only ask what <em>the authenticated user in front of it</em> may reach. The user is proven by the
/// Watchtower-signed assertion the stack was forwarded on the request it is serving — never by a user id in
/// the request — and the assertion's <c>aud</c> must be one of the calling stack's own domains, so an
/// assertion picked up elsewhere buys nothing.
/// </para>
/// <para>
/// The answer carries no stack ids, no status, no timestamps and no environment data: it is rendered to end
/// users in a tenant switcher, so it holds exactly what is needed to offer the switch.
/// </para>
/// </remarks>
/// <param name="db">Scoped Watchtower database context.</param>
/// <param name="signer">Verifies the forwarded identity assertion against the calling stack's domains.</param>
public sealed class TenantDiscoveryService(WatchtowerDbContext db, AuthTokenSigner signer) {
    /// <summary>
    /// The template <paramref name="stackId"/> is a tenant of, or <see langword="null"/> when it is not a
    /// tenant at all.
    /// </summary>
    /// <remarks>
    /// A tenant is a stack with both a template and a slug — the same definition the management API's
    /// listing uses, so a half-provisioned row is not treated as one here and skipped there.
    /// </remarks>
    /// <param name="stackId">The stack asking about its siblings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The template id, or null when the stack is standalone.</returns>
    public Task<int?> ResolveTenantTemplateIdAsync(int stackId, CancellationToken ct) =>
        db.Stacks.AsNoTracking()
            .Where(s => s.Id == stackId && s.TenantSlug != null)
            .Select(s => s.TemplateId)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Resolves the account a forwarded <c>X-Watchtower-Jwt</c> assertion proves, bound to the calling
    /// stack's own domains, or <see langword="null"/> when it proves nothing usable.
    /// </summary>
    /// <remarks>
    /// Three checks, and the caller must not be able to tell which one refused it. The signature, algorithm,
    /// issuer and expiry are the signer's; the <c>aud</c> must be a domain <paramref name="callerStackId"/>
    /// is itself served on, which is what stops a stack asking about users it has never seen; and the account
    /// is then reloaded, because the assertion is a five-minute-old statement while access is answered as of
    /// now — an account disabled in between must resolve to nothing.
    /// </remarks>
    /// <param name="callerStackId">The authenticated stack, whose route domains are the accepted audiences.</param>
    /// <param name="assertion">Raw assertion from the request header; may be null or empty.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The proven user's id, or null.</returns>
    public async Task<int?> ResolveAssertionSubjectAsync(
        int callerStackId, string? assertion, CancellationToken ct) {
        var audiences = await db.Routes.AsNoTracking()
            .Where(r => r.StackId == callerStackId)
            .Select(r => r.Domain)
            .ToListAsync(ct);

        // A stack Watchtower serves no domain for cannot have been forwarded an assertion in the first
        // place, and the signer refuses an empty audience set rather than reading it as "any".
        if (!signer.TryValidate(assertion, audiences, out var userId)) return null;

        var account = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.Disabled })
            .FirstOrDefaultAsync(ct);
        return account is null || account.Disabled ? null : account.Id;
    }

    /// <summary>
    /// The tenants of <paramref name="templateId"/> that <paramref name="userId"/> may enter, by slug
    /// ascending.
    /// </summary>
    /// <remarks>
    /// Access is decided on each tenant's primary route through
    /// <see cref="RouteAccessPolicy.AccessibleRouteIdsAsync"/>, in one grants query for the whole template.
    /// A tenant with no primary route is omitted: there is no domain to switch to, so offering it would only
    /// produce a dead entry in the switcher.
    /// </remarks>
    /// <param name="templateId">Template whose tenants are being listed.</param>
    /// <param name="userId">The proven user, already established as live and enabled.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The accessible tenants, ordered by slug.</returns>
    public async Task<IReadOnlyList<AccessibleTenant>> ListAccessibleTenantsAsync(
        int templateId, int userId, CancellationToken ct) {
        var tenants = await db.Stacks.AsNoTracking()
            .Where(s => s.TemplateId == templateId && s.TenantSlug != null)
            .Select(s => new { s.Id, Slug = s.TenantSlug! })
            .ToListAsync(ct);
        if (tenants.Count == 0) return [];

        var stackIds = tenants.Select(t => t.Id).ToList();
        var routes = await db.Routes.AsNoTracking()
            .Where(r => stackIds.Contains(r.StackId) && r.IsPrimary)
            .ToListAsync(ct);

        // One route per tenant, lowest id first: should a stack somehow carry two primary rows, which one
        // decides access must not depend on the order the provider happened to return them in.
        var primary = routes
            .GroupBy(r => r.StackId)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Id).First());

        var accessible = await RouteAccessPolicy.AccessibleRouteIdsAsync(db, [.. primary.Values], userId, ct);

        var reachable = new List<AccessibleTenant>(tenants.Count);
        foreach (var tenant in tenants) {
            if (!primary.TryGetValue(tenant.Id, out var route) || !accessible.Contains(route.Id)) continue;
            reachable.Add(new AccessibleTenant(tenant.Id, tenant.Slug, route.Domain));
        }

        // Ordinal, and sorted here rather than in SQL: slugs are already normalised to lowercase ASCII, and
        // a culture-sensitive comparison would make the order depend on the host's locale.
        reachable.Sort((left, right) => string.CompareOrdinal(left.Slug, right.Slug));
        return reachable;
    }
}
