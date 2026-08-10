using Elarion.Abstractions.Authorization;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>
/// Lets a stack manage a template's tenants through the public management API (<c>/api/mgmt/*</c>),
/// creating the grant or adjusting the capability of an existing one.
/// </summary>
/// <remarks>
/// <para>
/// <c>[RequireRole("Admin")]</c>: this is privilege management. The grantee authenticates with its own
/// App API token, which on its own reaches nothing but the self-only App API — this row is what turns
/// that token into a management credential, so only an operator may write it.
/// </para>
/// <para>
/// A stack that is itself an instance of the target template is refused. A tenant that could manage its
/// own template could enumerate, redeploy and (with delete) destroy its neighbours, which is exactly the
/// isolation the per-tenant stack boundary exists to provide.
/// </para>
/// <para>
/// The write is an upsert keyed on <c>(stack, template)</c>, so re-granting adjusts
/// <see cref="Command.AllowDelete"/> instead of adding a second row a later revoke could miss.
/// <c>CreatedAt</c> deliberately survives a capability change: it records when management was first
/// granted.
/// </para>
/// </remarks>
[Handler("templates.grantManagement")]
[RequireRole(WatchtowerClaims.AdminRole)]
public sealed class GrantManagement(WatchtowerDbContext db, TimeProvider time)
    : IHandler<GrantManagement.Command, Result<GrantManagement.Response>> {
    /// <summary>Which stack may manage which template's tenants, and how far.</summary>
    /// <param name="TemplateId">Template whose tenants become manageable.</param>
    /// <param name="StackId">Stack whose App API token is granted the management rights.</param>
    /// <param name="AllowDelete">When true the grantee may also deprovision tenants and purge their volumes.</param>
    public sealed record Command(int TemplateId, int StackId, bool AllowDelete);

    /// <summary>The stored grant.</summary>
    /// <param name="Grant">The grant as it now stands.</param>
    public sealed record Response(TemplateGrantDto Grant);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (!await db.StackTemplates.AnyAsync(t => t.Id == command.TemplateId, ct))
            return AppError.NotFound($"Template {command.TemplateId} not found");

        var stack = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == command.StackId)
            .Select(s => new { s.Id, s.Name, s.TemplateId })
            .FirstOrDefaultAsync(ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        if (stack.TemplateId == command.TemplateId)
            return AppError.Validation(
                $"Stack '{stack.Name}' is a tenant of this template and cannot be granted management of it.");

        var grant = await db.TemplateManagementGrants
            .FirstOrDefaultAsync(g => g.TemplateId == command.TemplateId && g.StackId == command.StackId, ct);

        if (grant is not null) {
            grant.AllowDelete = command.AllowDelete;
            await db.SaveChangesAsync(ct);
        } else {
            grant = new TemplateManagementGrant {
                TemplateId = command.TemplateId,
                StackId = command.StackId,
                AllowDelete = command.AllowDelete,
                CreatedAt = time.GetUtcNow(),
            };
            db.TemplateManagementGrants.Add(grant);
            try {
                await db.SaveChangesAsync(ct);
            } catch (DbUpdateException) {
                // Lost the insert race against a concurrent grant of the same pair. The unique index is
                // what caught it; recover by applying the capability to the row that won, so the caller
                // still observes the upsert it asked for rather than a spurious failure.
                db.Entry(grant).State = EntityState.Detached;
                grant = await db.TemplateManagementGrants.FirstOrDefaultAsync(
                    g => g.TemplateId == command.TemplateId && g.StackId == command.StackId, ct);
                if (grant is null) throw;
                grant.AllowDelete = command.AllowDelete;
                await db.SaveChangesAsync(ct);
            }
        }

        return new Response(new TemplateGrantDto(stack.Id, stack.Name, grant.AllowDelete, grant.CreatedAt));
    }
}
