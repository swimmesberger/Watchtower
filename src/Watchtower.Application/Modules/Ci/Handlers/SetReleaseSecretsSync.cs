using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Ci.Handlers;

/// <summary>
/// Turns the release-secret sync on or off for one product (docs/products/design.md §"Secret sync").
/// While it is on, the reconcile loop keeps the repository's <c>WATCHTOWER_URL</c> and
/// <c>WATCHTOWER_PRODUCT_ID</c> Actions variables and its <c>WATCHTOWER_RELEASE_TOKEN</c> secret equal
/// to what Watchtower knows, re-pushing whenever the token rotates.
/// </summary>
/// <remarks>
/// <para>
/// <b>In the Ci module, not Products, and its own method rather than a field on
/// <c>products.update</c>.</b> The module boundary follows <c>ci.enableForProduct</c>, which is
/// likewise product-scoped, likewise writes product columns and likewise audits under <c>ci</c>: the
/// thing being configured is the repository's Actions configuration, whose other contributor
/// (<c>ci.updateRepo</c>'s registry selection) and whose read model (<c>ci.getProductCi</c>) both live
/// here. And it is a separate method because enabling is not a field assignment — it resolves the CI
/// repo, refuses the monorepo conflict in words, probes the PAT for the two permissions writing needs,
/// and mints a token when there is none. Folding four fallible steps into the product edit form would
/// make an unrelated rename fail on a PAT problem.
/// </para>
/// <para>
/// Turning the sync off leaves the values already at GitHub alone — the same rule
/// <c>ci.updateRepo</c> follows for the registry secrets. Watchtower stops maintaining them; deleting
/// them is a repository decision, and silently revoking a running workflow's credentials on a toggle
/// would be the surprise.
/// </para>
/// </remarks>
[Handler("ci.setReleaseSecretsSync")]
public sealed class SetReleaseSecretsSync(
    WatchtowerDbContext db,
    CiRepoResolver resolver,
    GitHubApiClient gitHub,
    CiRunnerOrchestrator orchestrator,
    AuditLog audit,
    ICurrentUser currentUser)
    : IHandler<SetReleaseSecretsSync.Command, Result<SetReleaseSecretsSync.Response>> {
    /// <summary>Audit action recorded for a toggle.</summary>
    public const string AuditAction = "release-token.sync.toggle";

    public sealed record Command(int ProductId, bool Enabled);

    /// <param name="Ci">
    /// The product's whole CI view after the change, so the tab re-renders from one answer rather than
    /// patching a toggle and re-fetching the rest.
    /// </param>
    public sealed record Response(CiLinkDto Ci);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == command.ProductId, ct);
        if (product is null)
            return AppError.NotFound($"Product {command.ProductId} not found.");

        if (!command.Enabled)
            return await DisableAsync(product, ct);

        if (GitHubRepoUrl.TryParse(product.RepositoryUrl) is not var (owner, name)) {
            return AppError.Validation(
                $"Product '{product.Name}' does not deploy from a github.com repository "
                + $"({product.RepositoryUrl}), so there is no Actions configuration to sync into. Add the "
                + "release token to your CI by hand instead.");
        }

        var repo = await resolver.FindForWriteAsync(product, owner, name, ct);
        if (repo is null) {
            return AppError.Validation(
                $"CI runners are not enabled for {owner}/{name}. The sync writes with the same "
                + "fine-grained PAT the runners register with, so enable CI for this product first — or "
                + "add the release token to the repository by hand.");
        }

        // The monorepo rule, reported before the filtered unique index has to enforce it: the three
        // secret names are fixed, so a second syncing product of the same repository would overwrite
        // this one's token on the very next pass (design.md: "v2: name-suffixed secrets").
        //
        // Asked through the resolver rather than as `CiRepoId == repo.Id`, because the index and that
        // query see the same thing and both miss the same row: a product whose CI repo was deleted
        // keeps the flag while SET NULL drops its FK, and PostgreSQL treats NULLs as distinct. The
        // resolver matches the parsed URL too, so the stray is found and named here rather than
        // discovered later as two products overwriting one token.
        var conflict = (await resolver.FindSyncingProductsAsync(repo, ct))
            .FirstOrDefault(p => p.Id != product.Id);
        if (conflict is not null) {
            return AppError.Validation(
                $"Product '{conflict.Name}' already syncs its release secrets to {repo.FullName}. The "
                + $"Actions secret names are fixed ({CiActionsConfigSync.TokenSecret} and the two "
                + "WATCHTOWER_* variables), so only one product per repository can own them — turn the "
                + $"sync off for '{conflict.Name}' first, or add this product's token by hand.");
        }

        var credential = await db.Credentials.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == repo.CredentialId, ct);
        if (credential is null)
            return AppError.NotFound($"Credential {repo.CredentialId} not found.");

        // The same up-front discipline as ci.updateRepo's registry selection: writing secrets and
        // variables needs PAT permissions plain runners do not have, so it fails here with the missing
        // one named rather than as a background sync error nobody is watching for.
        if (await gitHub.ValidateSecretsAccessAsync(
                owner, name, credential.Token, CiActionsConfigSync.ReleaseFeature, ct) is { } accessError) {
            return AppError.Validation(
                $"Credential '{credential.Name}' cannot sync release secrets for {repo.FullName}: "
                + $"{accessError} You can still add {CiActionsConfigSync.TokenSecret} to the repository "
                + "by hand — the Releases tab shows the token and the exact settings path.");
        }

        // A sync with nothing to push is a trap, and so is a token the endpoint answers 404 for — the
        // same reasoning products.setReleaseWebhook applies when it generates one on enable.
        var generated = false;
        if (product.ReleaseWebhookToken is null) {
            product.ReleaseWebhookToken = ReleaseWebhookTokens.Generate();
            product.ReleaseWebhookEnabled = true;
            generated = true;
        }

        var wasEnabled = product.SyncReleaseSecrets;
        product.SyncReleaseSecrets = true;
        // Record the link the sync resolves through, so the filtered unique index has a value to
        // constrain from this moment on rather than from whenever a read path happens to fill it in.
        product.CiRepo = repo;
        ClearSyncState(product);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(
            CiActionsConfigSync.AuditCategory, AuditAction, repo.FullName,
            $"release secret sync {(wasEnabled ? "re-armed" : "enabled")} for product '{product.Name}'"
            + (generated ? "; token generated and webhook enabled" : string.Empty),
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        // Save-to-retry, exactly as ci.updateRepo does it: an operator who just granted the PAT its
        // permissions expects the next attempt now, not after the standing five-minute defer.
        orchestrator.ClearActionsSyncBackoff(repo.Id);
        orchestrator.RequestReconcile();
        return new Response(BuildLink(product, owner, name, repo));
    }

    private async ValueTask<Result<Response>> DisableAsync(Entities.Product product, CancellationToken ct) {
        var link = await resolver.ResolveAsync(product, ct);
        if (product.SyncReleaseSecrets) {
            product.SyncReleaseSecrets = false;
            ClearSyncState(product);
            await db.SaveChangesAsync(ct);
            await audit.RecordAsync(
                CiActionsConfigSync.AuditCategory, AuditAction, link.Repo?.FullName ?? product.Name,
                $"release secret sync disabled for product '{product.Name}'; the values already at "
                + "GitHub are left in place",
                actor: await audit.ActorAsync(currentUser, ct), ct: ct);
            // Both directions clear the defer. Disabling has nothing of its own to retry, but the timer
            // is shared with the registry contributor: leaving a defer this product's failures armed
            // standing after it stopped syncing would park a registry re-push for no reason at all.
            if (link.Repo is { } repo) {
                orchestrator.ClearActionsSyncBackoff(repo.Id);
                orchestrator.RequestReconcile();
            }
        }
        return new Response(BuildLink(product, link.Owner, link.Name, link.Repo));
    }

    /// <summary>
    /// Drops the hash and both stamps so the next pass re-pushes unconditionally. The hash is what
    /// makes a steady state cost no GitHub call, so clearing it is precisely "try again from scratch".
    /// </summary>
    private static void ClearSyncState(Entities.Product product) {
        product.ActionsSyncedHash = null;
        product.ActionsSyncedAt = null;
        product.LastActionsSyncError = null;
    }

    /// <summary>The same shape <c>ci.getProductCi</c> answers with, built from what this call already has.</summary>
    private CiLinkDto BuildLink(Entities.Product product, string? owner, string? name, Entities.CiRepo? repo) {
        var status = repo is not null && orchestrator.Status.TryGetValue(repo.Id, out var s) ? s : null;
        return new CiLinkDto(
            IsGitHub: owner is not null,
            owner,
            name,
            repo is null ? null : CiMapping.ToDto(repo, status),
            product.SyncReleaseSecrets,
            CiMapping.ToReleaseSecretsSyncDto(product),
            CiMapping.ReleaseSecretsSyncBlocked(product, repo));
    }
}
