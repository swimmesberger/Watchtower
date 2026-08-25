using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>
/// Starts a stopped stack: records <see cref="StackDesiredState.Running"/> as its desired state
/// (ADR-0025) and brings its existing containers back via <c>docker compose start</c> — no clone, no
/// pull, no recreate. A stack whose containers were never created (or were removed) has nothing to
/// start; it is re-enabled all the same, and the operator deploys to create them.
/// </summary>
[Handler("stacks.start")]
public sealed class StartStack(
    WatchtowerDbContext db, ComposeCliService compose, DockerEngineClient docker,
    AuditLog audit, ICurrentUser currentUser)
    : IHandler<StartStack.Command, Result<StartStack.Response>> {
    public sealed record Command(int Id);
    /// <param name="Started">
    /// True when containers were started; false when the project has none — the stack is re-enabled
    /// but needs a deploy to create its containers.
    /// </param>
    public sealed record Response(StackDto Stack, bool Started);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var stack = await db.Stacks
            .Include(s => s.UpdateCheck)
            .Include(s => s.Product)
            .Include(s => s.Template)
            .FirstOrDefaultAsync(s => s.Id == command.Id, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.Id} not found");

        // Persist first, start after — the reverse of stacks.stop, and deliberately so: were the
        // intent still Stopped while containers come up, a crash in between would leave a stack the
        // startup reconcile then re-stops against the operator's wishes. The benign converse — a
        // failed start leaves the stack re-enabled with its containers down — is answered by
        // retrying or deploying.
        stack.DesiredState = StackDesiredState.Running;
        await db.SaveChangesAsync(ct);

        // `compose start` can only start containers that exist; with none, compose fails instead of
        // no-opping (unlike stop/down). Distinguish the two up front so a never-deployed stack gets
        // re-enabled with a clear "deploy to create containers" answer rather than an error.
        IReadOnlyList<DockerContainerInfo> containers;
        try {
            containers = await docker.ListContainersByLabelsAsync(
                [$"{StackLifecycle.ComposeProjectLabel}={stack.ComposeProjectName}"], ct);
        } catch (HttpRequestException ex) {
            return AppError.Internal($"Docker Engine API error: {ex.Message}");
        }

        var started = containers.Count > 0;
        if (started) {
            var (exitCode, output) = await compose.StartProjectAsync(stack.ComposeProjectName, ct);
            if (exitCode != 0)
                return AppError.Internal(
                    $"docker compose start failed for '{stack.ComposeProjectName}' (exit {exitCode}): {StackLifecycle.Tail(output)}");
        }

        await audit.RecordAsync(StackLifecycle.AuditCategory, "stack.start", stack.Name,
            started
                ? $"stack started (compose project '{stack.ComposeProjectName}')"
                : $"stack re-enabled (compose project '{stack.ComposeProjectName}' has no containers — deploy to create them)",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(StackMapping.ToDto(stack, stack.UpdateCheck), started);
    }
}
