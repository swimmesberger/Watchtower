using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Modules.Credentials.Handlers;

/// <summary>Creates a new credential.</summary>
[Handler("credentials.create")]
public sealed class CreateCredential(WatchtowerDbContext db)
    : IHandler<CreateCredential.Command, Result<CreateCredential.Response>> {
    public sealed record Command(string Name, string Username, string Token);
    public sealed record Response(CredentialDto Credential);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var credential = new Credential {
            Name = command.Name,
            Username = command.Username,
            Token = command.Token,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Credentials.Add(credential);
        await db.SaveChangesAsync(ct);
        return new Response(new CredentialDto(credential.Id, credential.Name, credential.Username, credential.CreatedAt));
    }
}
