using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.System.Handlers;

/// <summary>
/// Persists the self-update configuration (image override, registry credential, compose overrides).
/// Pass null for a field to clear the override and revert to auto-detection.
/// </summary>
[Handler("system.updateConfig")]
public sealed class UpdateSelfConfiguration(SelfUpdateService selfUpdate)
    : IHandler<UpdateSelfConfiguration.Command, Result<UpdateSelfConfiguration.Response>> {
    public sealed record Command(
        string? ImageName, int? CredentialId, string? ComposeFilePath, string? ComposeProjectName);

    public sealed record Response(SelfUpdateStatus Status);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        selfUpdate.SaveConfig(new UpdateSelfConfig {
            ImageName = command.ImageName,
            CredentialId = command.CredentialId,
            ComposeFilePath = command.ComposeFilePath,
            ComposeProjectName = command.ComposeProjectName,
        });
        return new Response(await selfUpdate.GetStatusAsync(ct));
    }
}
