using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>
/// The CI view of one stack: whether its repository URL is a GitHub repo (only those can get
/// runners), and the linked <see cref="CiRepoDto"/> — runner status and toolchain profile included —
/// when CI is enabled for that repository. The link is implicit via <c>owner/name</c>: stacks
/// deploying the same repository share one CI repo, one runner pool and one toolcache.
/// </summary>
[Handler("ci.getStackCi")]
public sealed class GetStackCi(WatchtowerDbContext db, CiRunnerOrchestrator orchestrator)
    : IHandler<GetStackCi.Query, Result<GetStackCi.Response>> {
    public sealed record Query(int StackId) : IQuery;

    public sealed record Response(CiStackCiDto Ci);

    public async ValueTask<Result<Response>> HandleAsync(Query query, CancellationToken ct) {
        var stack = await db.Stacks.AsNoTracking().FirstOrDefaultAsync(s => s.Id == query.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {query.StackId} not found.");

        if (GitHubRepoUrl.TryParse(stack.RepositoryUrl) is not var (owner, name))
            return new Response(new CiStackCiDto(IsGitHub: false, Owner: null, Name: null, Repo: null));

        var repo = await db.CiRepos.AsNoTracking().FirstOrDefaultAsync(
            r => r.Owner.ToLower() == owner.ToLower() && r.Name.ToLower() == name.ToLower(), ct);
        var dto = repo is null
            ? null
            : CiMapping.ToDto(repo, orchestrator.Status.TryGetValue(repo.Id, out var s) ? s : null);
        return new Response(new CiStackCiDto(IsGitHub: true, owner, name, dto));
    }
}
