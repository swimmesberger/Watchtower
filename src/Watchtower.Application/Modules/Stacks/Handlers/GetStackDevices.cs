using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>Returns all host device mappings configured for a stack (ADR-0030).</summary>
[Handler("stacks.getDevices")]
public sealed class GetStackDevices(WatchtowerDbContext db)
    : IHandler<GetStackDevices.Query, Result<GetStackDevices.Response>> {
    public sealed record Query(int StackId);
    public sealed record Response(IReadOnlyList<StackDeviceMappingDto> Devices);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        if (!await db.Stacks.AnyAsync(s => s.Id == query.StackId, ct))
            return AppError.NotFound($"Stack {query.StackId} not found");

        var devices = await db.StackDeviceMappings.AsNoTracking()
            .Where(m => m.StackId == query.StackId)
            .OrderBy(m => m.Service).ThenBy(m => m.HostPath)
            .Select(m => new StackDeviceMappingDto(m.Id, m.Service, m.HostPath, m.ContainerPath, m.Permissions))
            .ToListAsync(ct);
        return new Response(devices);
    }
}
