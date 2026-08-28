using System.Text.RegularExpressions;
using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Stacks.Handlers;

/// <summary>
/// Atomically replaces all host device mappings of a stack (ADR-0030) — the <c>devices:</c> entries
/// the deploy renders into the generated compose override, kept in Watchtower because the values are
/// host-specific and must not live in the product's repository. Pass an empty list to clear them.
/// A mapping may name a service the current compose file does not contain — services come and go
/// with the repository — so the deploy warns rather than this handler refusing; validation here is
/// limited to what can never be right.
/// </summary>
/// <remarks>
/// Audited: mapping a host device into a container is an operator-level grant of host access, unlike
/// the plain configuration <c>stacks.setEnv</c> replaces silently.
/// </remarks>
[Handler("stacks.setDevices")]
public sealed partial class SetStackDevices(WatchtowerDbContext db, AuditLog audit, ICurrentUser currentUser)
    : IHandler<SetStackDevices.Command, Result<SetStackDevices.Response>> {
    public sealed record Command(int StackId, IReadOnlyList<StackDeviceMappingInput> Devices);
    public sealed record Response(IReadOnlyList<StackDeviceMappingDto> Devices);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var stack = await db.Stacks.FirstOrDefaultAsync(s => s.Id == command.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        var mappings = new List<StackDeviceMapping>(command.Devices.Count);
        // Duplicate host paths per service are never meaningful; duplicate container paths would
        // make Docker refuse the container at start — both are caught here, where the operator is.
        var hostSeen = new HashSet<(string, string)>();
        var containerSeen = new HashSet<(string, string)>();
        foreach (var input in command.Devices) {
            var service = input.Service.Trim();
            if (service.Length is 0 or > 128 || !ServiceNameRegex().IsMatch(service))
                return AppError.Validation(
                    $"'{input.Service}' is not a valid compose service name (letters, digits, '.', '_', '-'; at most 128 characters).");

            if (ValidateDevicePath(input.HostPath, "host path") is { } hostError) return hostError;
            var hostPath = input.HostPath.Trim();

            var containerPath = string.IsNullOrWhiteSpace(input.ContainerPath) ? hostPath : input.ContainerPath.Trim();
            if (ValidateDevicePath(containerPath, "container path") is { } containerError) return containerError;

            var permissions = string.IsNullOrWhiteSpace(input.Permissions)
                ? null : input.Permissions.Trim().ToLowerInvariant();
            if (permissions is not null && !PermissionsRegex().IsMatch(permissions))
                return AppError.Validation(
                    $"Invalid device permissions '{input.Permissions}' — expected a combination of 'r', 'w' and 'm' (e.g. \"rwm\").");

            if (!hostSeen.Add((service, hostPath)))
                return AppError.Validation($"Duplicate device: '{hostPath}' is mapped twice into service '{service}'.");
            if (!containerSeen.Add((service, containerPath)))
                return AppError.Validation(
                    $"Duplicate target: two devices of service '{service}' map to '{containerPath}' in the container.");

            mappings.Add(new StackDeviceMapping {
                StackId = stack.Id, Service = service,
                HostPath = hostPath, ContainerPath = containerPath, Permissions = permissions,
            });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.StackDeviceMappings.Where(m => m.StackId == stack.Id).ExecuteDeleteAsync(ct);
        db.StackDeviceMappings.AddRange(mappings);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        await audit.RecordAsync(StackLifecycle.AuditCategory, "stack.devices.update", stack.Name,
            mappings.Count == 0
                ? "device mappings cleared"
                : "device mappings replaced: "
                    + string.Join(", ", mappings.Select(m =>
                        $"{m.Service} ← {m.HostPath}"
                        + (m.ContainerPath == m.HostPath ? "" : $" at {m.ContainerPath}")
                        + (m.Permissions is { } p ? $" ({p})" : ""))),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        var saved = await db.StackDeviceMappings.AsNoTracking()
            .Where(m => m.StackId == stack.Id)
            .OrderBy(m => m.Service).ThenBy(m => m.HostPath)
            .Select(m => new StackDeviceMappingDto(m.Id, m.Service, m.HostPath, m.ContainerPath, m.Permissions))
            .ToListAsync(ct);
        return new Response(saved);
    }

    /// <summary>
    /// Rejects what can never be a device path: relative (Docker requires absolute), containing the
    /// <c>':'</c> that delimits Compose's <c>host:container:permissions</c> string form, or a line
    /// break. Existence on the host is deliberately not checked — the row may be written on a
    /// machine other than the one that deploys, and the deploy is where absence surfaces.
    /// </summary>
    private static AppError? ValidateDevicePath(string? value, string what) {
        var path = value?.Trim();
        if (string.IsNullOrEmpty(path)) return AppError.Validation($"A device {what} is required.");
        if (!path.StartsWith('/')) return AppError.Validation($"The device {what} '{value}' must be absolute (start with '/').");
        if (path.Contains(':')) return AppError.Validation($"The device {what} '{value}' must not contain ':'.");
        if (path.AsSpan().ContainsAny('\n', '\r')) return AppError.Validation($"The device {what} must not contain line breaks.");
        if (path.Length > 512) return AppError.Validation($"The device {what} is too long (at most 512 characters).");
        return null;
    }

    /// <summary>The compose-spec service name constraint, <c>^[a-zA-Z0-9._-]+$</c>.</summary>
    [GeneratedRegex("^[a-zA-Z0-9._-]+$")]
    private static partial Regex ServiceNameRegex();

    /// <summary>Some non-empty subset of <c>rwm</c>, each at most once.</summary>
    [GeneratedRegex("^(?!.*(.).*\\1)[rwm]{1,3}$")]
    private static partial Regex PermissionsRegex();
}
