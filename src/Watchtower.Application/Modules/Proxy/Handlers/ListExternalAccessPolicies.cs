using Elarion.Abstractions.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// Lists the reusable Access policies that already exist in the operator's Cloudflare account — the roster an
/// <c>externalPolicy</c> clause is picked from (ADR-0039), so a rule names <em>friends</em> instead of
/// carrying a UUID nobody can read back.
/// </summary>
/// <remarks>
/// Empty with no warning when the Cloudflare provider is not active or not configured: "not applicable" looks
/// like "nothing to pick" there, the same reading <see cref="ListCloudflareForeignRoutes"/> takes. When the
/// provider <em>is</em> active the emptiness is explained — a token without
/// <c>Access: Apps and Policies</c> permission cannot list them, and an account that simply has no reusable
/// policies is a different situation with the same empty list.
/// <para>
/// Fails open, like zone discovery: a listing error becomes a warning and an empty roster rather than an
/// error page, because an operator who knows the id can still type it in full and a picker is a convenience.
/// </para>
/// </remarks>
[Handler("proxy.listExternalAccessPolicies")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class ListExternalAccessPolicies(
    CloudflareApiClient api,
    IOptionsMonitor<WatchtowerOptions> options,
    ILogger<ListExternalAccessPolicies> logger)
    : IHandler<ListExternalAccessPolicies.Query, Result<ListExternalAccessPolicies.Response>> {
    public sealed record Query;

    /// <param name="Policies">The account's reusable policies; empty when there are none to offer.</param>
    /// <param name="Warning">Why the list is empty when that emptiness is worth explaining; else null.</param>
    public sealed record Response(IReadOnlyList<ExternalAccessPolicyDto> Policies, string? Warning = null);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var proxy = options.CurrentValue.Proxy;
        if (!proxy.Enabled || proxy.ResolveProvider() != ProxyProviderKind.Cloudflare)
            return new Response([]);

        var cf = proxy.Cloudflare;
        if (string.IsNullOrWhiteSpace(cf.AccountId) || string.IsNullOrWhiteSpace(cf.ApiToken)) {
            return new Response([],
                "Cloudflare is selected but the account id or API token is not configured, so its reusable "
                + "Access policies cannot be listed.");
        }

        try {
            var policies = await api.ListReusableAccessPoliciesAsync(cf.AccountId, cf.ApiToken, ct);
            // A policy with no name has nothing to pick it by, so it is dropped rather than shown as a blank
            // row: the point of this call is the names.
            var named = policies
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new ExternalAccessPolicyDto(p.Id, p.Name!.Trim(), p.Decision))
                .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new Response(named, named.Count > 0
                ? null
                : "The Cloudflare account has no reusable Access policies. Create one in the Zero Trust "
                  + "dashboard, or build the allow-list from Watchtower's own clauses instead.");
        } catch (Exception ex) {
            logger.LogWarning(ex, "Could not list the account's reusable Access policies.");
            return new Response([],
                "Could not list the account's reusable Access policies — the API token may lack the "
                + $"'Access: Apps and Policies' permission. ({ex.Message}) You can still enter a policy id.");
        }
    }
}
