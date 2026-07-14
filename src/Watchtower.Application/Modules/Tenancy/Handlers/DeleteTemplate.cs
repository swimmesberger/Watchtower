using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>
/// Deletes a template. Existing tenant stacks are detached (their TemplateId is set null via the FK),
/// not deleted — their containers keep running.
/// </summary>
[Handler("templates.delete")]
public sealed class DeleteTemplate(WatchtowerDbContext db)
    : IHandler<DeleteTemplate.Command, Result<DeleteTemplate.Response>> {
    public sealed record Command(int Id);
    public sealed record Response(int Id);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var deleted = await db.StackTemplates.Where(t => t.Id == command.Id).ExecuteDeleteAsync(ct);
        return deleted == 0
            ? AppError.NotFound($"Template {command.Id} not found")
            : new Response(command.Id);
    }
}
