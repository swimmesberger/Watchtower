using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>Repos visible to a credential's PAT, for the add-repo picker.</summary>
[Handler("ci.listAvailableRepos")]
public sealed class ListAvailableRepos(WatchtowerDbContext db, GitHubApiClient gitHub)
    : IHandler<ListAvailableRepos.Query, Result<ListAvailableRepos.Response>> {
    public sealed record Query(int CredentialId) : IQuery;

    public sealed record Response(IReadOnlyList<CiAvailableRepoDto> Repos);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var credential = await db.Credentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == query.CredentialId, ct);
        if (credential is null)
            return AppError.NotFound($"Credential {query.CredentialId} not found.");

        try {
            var repos = await gitHub.ListAccessibleReposAsync(credential.Token, ct);
            return new Response(repos
                .Select(r => new CiAvailableRepoDto(r.FullName, r.Private, r.DefaultBranch, r.PushedAt))
                .ToList());
        } catch (HttpRequestException ex) {
            return AppError.BusinessRule($"GitHub API request failed: {ex.Message}");
        }
    }
}
