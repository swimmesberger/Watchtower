using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Persists the self-update configuration (registry credential).
/// Pass null to clear and revert to an unauthenticated pull.
/// </summary>
[Handler("system.updateConfig")]
public sealed class UpdateSelfConfiguration(SelfUpdateService selfUpdate)
    : IHandler<UpdateSelfConfiguration.Command, Result<UpdateSelfConfiguration.Response>> {
    public sealed record Command(int? CredentialId);

    public sealed record Response(SelfUpdateStatus Status);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        await selfUpdate.SaveConfigAsync(new UpdateSelfConfig { CredentialId = command.CredentialId }, ct);
        return new Response(await selfUpdate.GetStatusAsync(ct));
    }
}
