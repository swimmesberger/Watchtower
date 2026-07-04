using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Credentials.Handlers;

/// <summary>Updates a credential. A null token keeps the existing value.</summary>
[Handler("credentials.update")]
public sealed class UpdateCredential(WatchtowerDbContext db)
    : IHandler<UpdateCredential.Command, Result<UpdateCredential.Response>> {
    public sealed record Command(int Id, string Name, string Username, string? Token);
    public sealed record Response(CredentialDto Credential);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var credential = await db.Credentials.FirstOrDefaultAsync(c => c.Id == command.Id, ct);
        if (credential is null)
            return AppError.NotFound($"Credential {command.Id} not found");

        credential.Name = command.Name;
        credential.Username = command.Username;
        if (!string.IsNullOrEmpty(command.Token))
            credential.Token = command.Token;
        await db.SaveChangesAsync(ct);

        return new Response(new CredentialDto(credential.Id, credential.Name, credential.Username, credential.CreatedAt));
    }
}
