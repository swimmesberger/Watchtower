using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>Enqueues a deploy for every tenant of a template (a fan-out rollout).</summary>
[Handler("templates.deployAll")]
public sealed class DeployAllTenants(WatchtowerDbContext db, DeployQueueService deployQueue)
    : IHandler<DeployAllTenants.Command, Result<DeployAllTenants.Response>> {
    public sealed record Command(int TemplateId);
    public sealed record Response(int Count);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var ids = await db.Stacks.AsNoTracking()
            .Where(s => s.TemplateId == command.TemplateId)
            .Select(s => s.Id)
            .ToListAsync(ct);
        foreach (var id in ids)
            deployQueue.Enqueue(id, "template-deploy");
        return new Response(ids.Count);
    }
}
