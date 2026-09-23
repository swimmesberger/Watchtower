using Elarion.Abstractions.Authorization;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Proxy.Handlers;

/// <summary>
/// What an access policy for a <em>new</em> route on a stack may name: the realm its grants and rules must
/// come from, and the enforcement point that will decide it. The create-form counterpart of the context
/// <see cref="GetAccess"/> returns for a route that already exists.
/// </summary>
/// <remarks>
/// Resolved by <see cref="RouteAccessValidation.StackRealmIdAsync"/> — the very function
/// <c>proxy.createRoute</c> validates against — so a form that offers only this realm's users, groups and
/// rules offers exactly the candidates the create will accept, and the cross-realm refusal is something the
/// caller never has to run into. A query of its own rather than a field on the stack listing, which is read
/// in many places that would each have to load the stack's category to answer it correctly.
/// <para>
/// Same <c>[RequireRole("Admin")]</c> as the rest of the access surface: naming a policy at create is
/// admin-only, so there is nothing here for anyone else to use.
/// </para>
/// </remarks>
[Handler("proxy.getStackAccessContext")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class GetStackAccessContext(WatchtowerDbContext db, IOptionsMonitor<WatchtowerOptions> options)
    : IHandler<GetStackAccessContext.Query, Result<GetStackAccessContext.Response>> {
    public sealed record Query(int StackId);

    public sealed record Response(int RealmId, ActiveEnforcementPoint ActiveEnforcementPoint);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var realmId = await RouteAccessValidation.StackRealmIdAsync(db, query.StackId, ct);
        if (realmId is null)
            return AppError.NotFound($"Stack {query.StackId} not found");
        return new Response(
            realmId.Value,
            AccessRuleMapping.Active(AccessClauseSupport.PointFor(options.CurrentValue.Proxy.ResolveProvider())));
    }
}
