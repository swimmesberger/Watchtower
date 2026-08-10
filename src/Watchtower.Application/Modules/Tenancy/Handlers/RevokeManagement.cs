using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>
/// Withdraws a stack's permission to manage a template's tenants. The grantee's App API token keeps
/// working for its own stack — the token was never the management credential, this row was.
/// </summary>
/// <remarks>
/// <c>[RequireRole("Admin")]</c> like the rest of the grant surface, and idempotent: revoking a grant
/// that is not there reports <c>Removed = false</c> rather than failing, so an operator (or a script)
/// converging on "this stack must not manage that template" never has to check first.
/// </remarks>
[Handler("templates.revokeManagement")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class RevokeManagement(WatchtowerDbContext db)
    : IHandler<RevokeManagement.Command, Result<RevokeManagement.Response>> {
    /// <summary>Which grant to withdraw.</summary>
    /// <param name="TemplateId">Template the grant is on.</param>
    /// <param name="StackId">Stack the grant belongs to.</param>
    public sealed record Command(int TemplateId, int StackId);

    /// <summary>Whether a grant was actually there to remove.</summary>
    /// <param name="Removed">True when a grant existed and is now gone; false when there was none.</param>
    public sealed record Response(bool Removed);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var deleted = await db.TemplateManagementGrants
            .Where(g => g.TemplateId == command.TemplateId && g.StackId == command.StackId)
            .ExecuteDeleteAsync(ct);
        return new Response(deleted > 0);
    }
}
