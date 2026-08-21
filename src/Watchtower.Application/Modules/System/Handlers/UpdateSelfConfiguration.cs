using Elarion.Abstractions.Identity;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Persists the self-update configuration (registry credential).
/// Pass null to clear and revert to an unauthenticated pull.
/// </summary>
[Handler("system.updateConfig")]
public sealed class UpdateSelfConfiguration(SelfUpdateService selfUpdate, AuditLog audit, ICurrentUser currentUser)
    : IHandler<UpdateSelfConfiguration.Command, Result<UpdateSelfConfiguration.Response>> {
    public sealed record Command(int? CredentialId);

    public sealed record Response(SelfUpdateStatus Status);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        await selfUpdate.SaveConfigAsync(new UpdateSelfConfig { CredentialId = command.CredentialId }, ct);
        await audit.RecordAsync("system", "self-update.config.update", "self-update settings",
            command.CredentialId is { } id ? $"registry credential #{id}" : "registry credential cleared",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);
        return new Response(await selfUpdate.GetStatusAsync(ct));
    }
}
