using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>
/// Updates a stack's public App API settings: switch <c>/api/app/*</c> access on or off, and/or
/// rotate the bearer token. A rotated token only reaches the application on its next deploy, so the
/// running containers keep presenting the previous value until then and will receive 401 responses.
/// </summary>
[Handler("stacks.setAppApi")]
public sealed class SetStackAppApi(
    WatchtowerDbContext db, AppApiService appApi, IOptions<WatchtowerOptions> options)
    : IHandler<SetStackAppApi.Command, Result<SetStackAppApi.Response>> {
    /// <summary>Request: the change to apply. Omitted fields are left untouched.</summary>
    /// <param name="StackId">Stack id.</param>
    /// <param name="Enabled">New enabled state; null leaves it unchanged.</param>
    /// <param name="RegenerateToken">When true, replaces the token with a freshly generated one.</param>
    public sealed record Command(int StackId, bool? Enabled, bool? RegenerateToken);

    /// <summary>App API settings after the change.</summary>
    /// <param name="Enabled">When false, every <c>/api/app/*</c> call with this token is refused with 403.</param>
    /// <param name="Token">The stack's bearer token, injected as <c>WATCHTOWER_APP_TOKEN</c>.</param>
    /// <param name="InjectedVarNames">Reserved variable names this stack's deploys write into its environment.</param>
    public sealed record Response(bool Enabled, string Token, IReadOnlyList<string> InjectedVarNames);

    /// <summary>Applies the change and returns the resulting settings.</summary>
    /// <param name="command">The change to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The updated settings, or <c>NotFound</c> when the stack does not exist.</returns>
    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var current = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == command.StackId)
            .Select(s => (bool?)s.AppApiEnabled)
            .FirstOrDefaultAsync(ct);
        if (current is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        var enabled = current.Value;
        if (command.Enabled is { } requested && requested != enabled) {
            await db.Stacks.Where(s => s.Id == command.StackId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AppApiEnabled, requested), ct);
            enabled = requested;
        }

        var token = command.RegenerateToken == true
            ? await appApi.RegenerateTokenAsync(command.StackId, ct)
            : await appApi.EnsureTokenAsync(command.StackId, ct);

        return new Response(
            enabled, token, AppApiTokens.InjectedVariableNames(options.Value.PublicBaseUrl));
    }
}
