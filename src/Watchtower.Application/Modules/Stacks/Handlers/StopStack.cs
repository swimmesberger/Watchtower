using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>
/// Stops a whole stack and records <see cref="StackDesiredState.Stopped"/> as its desired state
/// (ADR-0025) — the "disable" operation for stacks not currently in use. Containers are stopped via
/// <c>docker compose stop</c> (kept, not removed, so a later start is fast and loses nothing), every
/// deploy path rejects the stack while it is stopped, and the startup reconcile re-stops containers
/// that a Docker restart policy brought back after a host reboot.
/// </summary>
[Handler("stacks.stop")]
public sealed class StopStack(
    WatchtowerDbContext db, ComposeCliService compose, DockerEngineClient docker,
    AuditLog audit, ICurrentUser currentUser)
    : IHandler<StopStack.Command, Result<StopStack.Response>> {
    public sealed record Command(int Id);
    public sealed record Response(StackDto Stack);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var stack = await db.Stacks
            .Include(s => s.UpdateCheck)
            .Include(s => s.Product)
            .Include(s => s.Template)
            .FirstOrDefaultAsync(s => s.Id == command.Id, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.Id} not found");

        // A project with no containers has nothing to stop — skip the compose call, symmetric with
        // stacks.start's empty-project path, so "disabling" a never-deployed stack is a pure
        // database write.
        IReadOnlyList<DockerContainerInfo> containers;
        try {
            containers = await docker.ListContainersByLabelsAsync(
                [$"{StackLifecycle.ComposeProjectLabel}={stack.ComposeProjectName}"], ct);
        } catch (HttpRequestException ex) {
            return AppError.Internal($"Docker Engine API error: {ex.Message}");
        }

        // Stop first, persist after: the stack only counts as stopped once its containers actually
        // are, and a failed stop must leave the intent untouched — a Stopped row over running
        // containers would make the next startup reconcile "finish" a stop that never happened.
        if (containers.Count > 0) {
            var (exitCode, output) = await compose.StopProjectAsync(stack.ComposeProjectName, ct);
            if (exitCode != 0)
                return AppError.Internal(
                    $"docker compose stop failed for '{stack.ComposeProjectName}' (exit {exitCode}): {StackLifecycle.Tail(output)}");
        }

        stack.DesiredState = StackDesiredState.Stopped;
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(StackLifecycle.AuditCategory, "stack.stop", stack.Name,
            $"stack stopped (compose project '{stack.ComposeProjectName}') — deploys are rejected until it is started again",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(StackMapping.ToDto(stack, stack.UpdateCheck));
    }
}
