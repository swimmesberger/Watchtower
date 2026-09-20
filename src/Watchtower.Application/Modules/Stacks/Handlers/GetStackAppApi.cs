using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>
/// Returns a stack's public App API settings: whether <c>/api/app/*</c> accepts its token, the token
/// itself, and the reserved environment variables each deploy injects. The token is generated and
/// persisted on first read so an operator can hand it to the application before its first deploy.
/// </summary>
[Handler("stacks.getAppApi")]
public sealed class GetStackAppApi(
    WatchtowerDbContext db, AppApiService appApi, IOptions<WatchtowerOptions> options)
    : IHandler<GetStackAppApi.Query, Result<GetStackAppApi.Response>> {
    /// <summary>Request: which stack to read.</summary>
    /// <param name="StackId">Stack id.</param>
    public sealed record Query(int StackId);

    /// <summary>App API settings for a stack.</summary>
    /// <param name="Enabled">When false, every <c>/api/app/*</c> call with this token is refused with 403.</param>
    /// <param name="Token">The stack's bearer token, injected as <c>WATCHTOWER_APP_TOKEN</c>.</param>
    /// <param name="InjectedVariables">
    /// Exactly what this stack's next deploy writes into its environment, name and value, in write
    /// order. A preview rather than a record of the last deploy: it is resolved from the current
    /// settings and the stack's current routes, so a route protected since the last deploy already
    /// shows its audience here.
    /// </param>
    public sealed record Response(
        bool Enabled, string Token, IReadOnlyList<AppApiTokens.InjectedVariable> InjectedVariables);

    /// <summary>Reads the settings, materializing the token when the stack has none yet.</summary>
    /// <param name="query">The stack to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stack's App API settings, or <c>NotFound</c> when the stack does not exist.</returns>
    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var enabled = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == query.StackId)
            .Select(s => (bool?)s.AppApiEnabled)
            .FirstOrDefaultAsync(ct);
        if (enabled is null)
            return AppError.NotFound($"Stack {query.StackId} not found");

        var token = await appApi.EnsureTokenAsync(query.StackId, ct);
        var routes = await RouteAudiencesAsync(query.StackId, ct);
        return new Response(
            enabled.Value, token,
            AppApiTokens.InjectedVariables(options.Value, query.StackId, token, routes));
    }

    /// <summary>
    /// The stack's routes, reduced to what <c>AppApiTokens.ResolveAudience</c> reads. Queried here
    /// rather than inferred, so the preview names exactly the variables the next deploy will write.
    /// </summary>
    private Task<List<AppApiTokens.RouteAudience>> RouteAudiencesAsync(int stackId, CancellationToken ct) =>
        db.Routes.AsNoTracking()
            .Where(r => r.StackId == stackId)
            .Select(r => new AppApiTokens.RouteAudience(r.Domain, r.AccessMode, r.AccessAud))
            .ToListAsync(ct);
}
