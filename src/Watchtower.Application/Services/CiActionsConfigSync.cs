using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Converges one <see cref="CiRepo"/>'s GitHub Actions configuration on what Watchtower knows —
/// the per-repo pass <see cref="CiRunnerOrchestrator"/> runs beside the runner reconcile
/// (docs/ci-runners/design.md §Secrets, docs/products/design.md §"Secret sync").
/// </summary>
/// <remarks>
/// <para>
/// Two <em>independent</em> contributors write into the same repository's Actions config:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>registry</b> — the <c>REGISTRY</c> variable plus the <c>REGISTRY_USERNAME</c>/
/// <c>REGISTRY_PASSWORD</c> secrets, from the merged registry view, with its state on
/// <see cref="CiRepo"/>.
/// </description></item>
/// <item><description>
/// <b>release</b> — the <c>WATCHTOWER_URL</c> and <c>WATCHTOWER_PRODUCT_ID</c> variables plus the
/// <c>WATCHTOWER_RELEASE_TOKEN</c> secret, from the product that syncs into this repo, with its state
/// on <see cref="Product"/>.
/// </description></item>
/// </list>
/// <para>
/// Independent means two things, both load-bearing. Each contributor guards its push with <em>its
/// own</em> hash, so rotating a registry credential does not re-push a release token and rotating a
/// release token does not re-push registry credentials. And each runs inside its own try/catch and
/// its own scope, so neither can take the other — or the runner reconcile around them — down with it.
/// </para>
/// <para>
/// Extracted from the orchestrator (rather than left as private methods on a
/// <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>) so the pass can be driven directly by
/// a test with a stubbed <see cref="GitHubApiClient"/>: what these two contributors push, and when
/// they decline to push, is the whole feature.
/// </para>
/// </remarks>
public sealed class CiActionsConfigSync(
    IServiceScopeFactory scopeFactory,
    GitHubApiClient gitHub,
    IOptionsMonitor<WatchtowerOptions> options,
    AuditLog audit,
    ILogger<CiActionsConfigSync> logger) {

    /// <summary>Audit category both contributors record under — CI writes, whichever value they carry.</summary>
    public const string AuditCategory = "ci";

    /// <summary>Audit action of the registry contributor.</summary>
    public const string RegistryAuditAction = "registry.sync";

    /// <summary>Audit action of the release contributor.</summary>
    public const string ReleaseAuditAction = "release-token.sync";

    /// <summary>Actions variable holding Watchtower's public base URL.</summary>
    public const string UrlVariable = "WATCHTOWER_URL";

    /// <summary>Actions variable holding the product id the workflow reports releases for.</summary>
    public const string ProductIdVariable = "WATCHTOWER_PRODUCT_ID";

    /// <summary>Actions secret holding the product's release-webhook bearer token.</summary>
    public const string TokenSecret = "WATCHTOWER_RELEASE_TOKEN";

    /// <summary>
    /// How many times the release stamp may be re-staged after losing the <c>xmin</c> race with a
    /// concurrent product edit. Two: the retry re-reads the row, so losing twice means a writer editing
    /// that one product continuously — at which point the standing behaviour (log it, re-push on the
    /// next pass) is the honest answer. See <see cref="StampReleaseSyncAsync"/>.
    /// </summary>
    private const int MaxStampAttempts = 2;

    /// <summary>
    /// What the registry contributor calls itself in operator-facing text — the PAT-permission probe's
    /// message and its 403 explanation. A constant so the handler, the sync and the tests cannot drift
    /// into three different names for one thing.
    /// </summary>
    public const string RegistryFeature = "registry sync";

    /// <summary>What the release contributor calls itself in operator-facing text.</summary>
    public const string ReleaseFeature = "release secret sync";

    /// <summary>
    /// The message a product carries while <c>Watchtower:PublicBaseUrl</c> is unset. Durable rather
    /// than a log line: the workflow would post to an empty URL, and the operator has to be told.
    /// </summary>
    public const string PublicBaseUrlMissing =
        "Set Watchtower:PublicBaseUrl — until this instance knows its own public address there is no "
        + "value to put in the WATCHTOWER_URL variable, and the workflow's curl would have nowhere to post.";

    /// <summary>
    /// Runs both contributors for one repo. Never throws for a contributor's own failure: each is
    /// isolated, and a repo whose Actions config cannot be written still gets its runners.
    /// </summary>
    /// <param name="repo">The repo, loaded with its <see cref="CiRepo.Credential"/>.</param>
    /// <param name="status">The orchestrator's live state for this repo; carries the shared retry defer.</param>
    /// <remarks>
    /// The two <c>try</c> blocks are written out rather than folded into a helper so each keeps a
    /// literal message template. The registry one is the template this pass has always logged under,
    /// and log queries — and anything grouping by template — key on it.
    /// </remarks>
    public async Task SyncActionsConfigAsync(CiRepo repo, CiRepoRunnerStatus status, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(status);
        // Failure isolation, once per contributor. Cancellation propagates (that is shutdown, not a
        // failure); everything else is logged and dropped so the next contributor — and the next
        // pass — still runs.
        try {
            await SyncRegistryAsync(repo, status, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogWarning(ex, "Registry secret sync failed for CI repo {Repo}", repo.FullName);
        }
        try {
            await SyncReleaseSecretsAsync(repo, status, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogWarning(ex, "Release secret sync failed for CI repo {Repo}", repo.FullName);
        }
    }

    // ── Contributor 1: registry credentials (docs/ci-runners/design.md, Secrets §1) ──

    /// <summary>
    /// Pushes the selected registry's credentials to the repo's GitHub Actions config: the
    /// <c>REGISTRY</c> variable plus the sealed-box <c>REGISTRY_USERNAME</c>/<c>REGISTRY_PASSWORD</c>
    /// secrets. Values come from the merged registry view (host docker config + Watchtower registries)
    /// at every pass, so a rotated credential re-pushes automatically via the hash compare — no GitHub
    /// call happens while the hash matches. Runs independently of <see cref="CiRepo.Enabled"/> so a
    /// temporarily disabled repo cannot drift.
    /// </summary>
    private async Task SyncRegistryAsync(CiRepo repo, CiRepoRunnerStatus status, CancellationToken ct) {
        if (repo.SyncRegistryUrl is null || repo.Credential is null)
            return;
        if (status.ActionsSyncRetryAt is { } retryAt && retryAt > DateTimeOffset.UtcNow)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var resolved = (await scope.ServiceProvider.GetRequiredService<RegistryAuthBuilder>()
                .ListResolvedRegistriesAsync(ct))
            .FirstOrDefault(r => string.Equals(r.Url, repo.SyncRegistryUrl, StringComparison.OrdinalIgnoreCase));

        var tracked = await db.CiRepos.FirstOrDefaultAsync(r => r.Id == repo.Id, ct);
        if (tracked is null)
            return;

        if (resolved is null || resolved.Username is null || resolved.Password is null) {
            // Local state, so it does not arm the defer — see RecordRegistryFailureAsync's remarks.
            await RecordRegistryFailureAsync(db, tracked, status,
                $"Registry '{repo.SyncRegistryUrl}' no longer resolves to a credential — it was removed "
                + "from the host docker config or its Watchtower registry lost its credential.",
                deferRetry: false, ct);
            return;
        }

        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{resolved.Url}\n{resolved.Username}\n{resolved.Password}")));
        if (hash == tracked.RegistrySyncedHash && tracked.LastRegistrySyncError is null)
            return;

        try {
            var token = repo.Credential.Token;
            var key = await gitHub.GetActionsPublicKeyAsync(repo.Owner, repo.Name, token, ct);
            await gitHub.PutActionsSecretAsync(repo.Owner, repo.Name, "REGISTRY_USERNAME",
                GitHubSecretSealer.Seal(key.Key, resolved.Username), key.KeyId, token, ct);
            await gitHub.PutActionsSecretAsync(repo.Owner, repo.Name, "REGISTRY_PASSWORD",
                GitHubSecretSealer.Seal(key.Key, resolved.Password), key.KeyId, token, ct);
            await gitHub.SetActionsVariableAsync(repo.Owner, repo.Name, "REGISTRY", resolved.Url, token, ct);

            tracked.RegistrySyncedHash = hash;
            tracked.RegistrySyncedAt = DateTimeOffset.UtcNow;
            tracked.LastRegistrySyncError = null;
            status.ClearActionsSyncRetry();
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Synced registry {Registry} to GitHub Actions config of {Repo}",
                resolved.Url, repo.FullName);
            // Actor-less: nobody clicked this — the reconcile loop shipped credentials to GitHub,
            // which is exactly the kind of outward write the trail exists for.
            await audit.RecordAsync(AuditCategory, RegistryAuditAction, repo.FullName,
                $"pushed the REGISTRY variable and REGISTRY_USERNAME/REGISTRY_PASSWORD secrets for "
                + $"'{resolved.Url}' to GitHub Actions", ct: ct);
        } catch (HttpRequestException ex) {
            await RecordRegistryFailureAsync(db, tracked, status, Explain(ex, RegistryFeature), deferRetry: true, ct);
        }
    }

    /// <summary>
    /// Persists a standing registry-sync failure, audits the <em>transition</em> only, and arms the
    /// shared retry defer when — and only when — a GitHub call is what failed.
    /// </summary>
    /// <param name="deferRetry">
    /// True for a failed GitHub call, which is what the defer exists to rate-limit. False for a local,
    /// permanent-until-an-operator-acts state: no round-trip was spent, re-evaluating it next pass costs
    /// a query the pass already makes, and — because the timer is shared — arming it would park the
    /// <em>other</em> contributor for five minutes over a problem that is none of its business. Writing
    /// the same message again is a no-op in the change tracker, so the repeated <c>SaveChanges</c> issues
    /// no SQL.
    /// </param>
    private async Task RecordRegistryFailureAsync(
        WatchtowerDbContext db, CiRepo tracked, CiRepoRunnerStatus status, string message,
        bool deferRetry, CancellationToken ct) {
        // Audited on transitions only: the retry loop re-fails with the same message every few
        // minutes, and a row per attempt would evict the category's actual history (the CI tab
        // already shows the standing error).
        var isNewFailure = tracked.LastRegistrySyncError != message;
        tracked.LastRegistrySyncError = message;
        if (deferRetry)
            status.DeferActionsSyncRetry();
        await db.SaveChangesAsync(ct);
        if (isNewFailure) {
            await audit.RecordAsync(AuditCategory, RegistryAuditAction, tracked.FullName,
                $"syncing registry '{tracked.SyncRegistryUrl}' to GitHub Actions failed",
                success: false, error: message, ct: ct);
        }
    }

    // ── Contributor 2: release configuration (docs/products/design.md §"Secret sync") ──

    /// <summary>
    /// Pushes the syncing product's release configuration to the repo's Actions config: the
    /// <c>WATCHTOWER_URL</c> and <c>WATCHTOWER_PRODUCT_ID</c> variables plus the sealed-box
    /// <c>WATCHTOWER_RELEASE_TOKEN</c> secret, which is exactly what the workflow step in
    /// docs/products/design.md reads. Its own hash — over base URL, product id and token — so a
    /// registry rotation never re-pushes a token, and independent of <see cref="CiRepo.Enabled"/> for
    /// the same reason the registry contributor is: a disabled repo must not silently drift.
    /// </summary>
    private async Task SyncReleaseSecretsAsync(CiRepo repo, CiRepoRunnerStatus status, CancellationToken ct) {
        if (repo.Credential is null)
            return;
        if (status.ActionsSyncRetryAt is { } retryAt && retryAt > DateTimeOffset.UtcNow)
            return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var resolver = scope.ServiceProvider.GetRequiredService<CiRepoResolver>();

        var candidates = await resolver.FindSyncingProductsAsync(repo, ct);
        if (candidates.Count == 0)
            return;
        if (candidates.Count > 1) {
            // The monorepo rule, enforced where it actually bites. The filtered unique index cannot see
            // a row whose ci_repo_id went NULL (PostgreSQL treats NULLs as distinct), which is exactly
            // what ci.removeRepo's SET NULL used to leave behind — and picking the lowest id would then
            // push one product's token into the repository the other one was wired for. Neither is
            // synced, and both are told why, because there is no way to know which one the operator
            // meant.
            var names = string.Join(", ", candidates.Select(p => $"'{p.Name}'"));
            foreach (var ambiguous in candidates) {
                await RecordReleaseFailureAsync(db, repo, ambiguous, status,
                    $"{candidates.Count} products ({names}) are all set to sync their release secrets to "
                    + $"{repo.FullName}, and the Actions secret names are fixed — so pushing either one's "
                    + "token would overwrite the other's. Nothing was pushed. Turn the sync off for all "
                    + "but one of them on its CI tab.",
                    deferRetry: false, ct);
            }
            return;
        }
        var product = candidates[0];

        var baseUrl = options.CurrentValue.PublicBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl)) {
            // Local states, both of them: no GitHub call was spent and no amount of retrying fixes
            // them, so they record the durable error without arming the shared defer.
            await RecordReleaseFailureAsync(db, repo, product, status, PublicBaseUrlMissing, deferRetry: false, ct);
            return;
        }
        if (product.ReleaseWebhookToken is not { } releaseToken) {
            await RecordReleaseFailureAsync(db, repo, product, status,
                $"Product '{product.Name}' has no release token to sync. Generate one on its Releases tab.",
                deferRetry: false, ct);
            return;
        }

        // The three values, in one hash. Nothing about the registry contributor's state is in it, and
        // nothing about this is in the registry hash — that is the independence rule, spelled.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{baseUrl}\n{product.Id.ToString(CultureInfo.InvariantCulture)}\n{releaseToken}")));
        if (hash == product.ActionsSyncedHash && product.LastActionsSyncError is null)
            return;

        try {
            var pat = repo.Credential.Token;
            var key = await gitHub.GetActionsPublicKeyAsync(repo.Owner, repo.Name, pat, ct);
            await gitHub.PutActionsSecretAsync(repo.Owner, repo.Name, TokenSecret,
                GitHubSecretSealer.Seal(key.Key, releaseToken), key.KeyId, pat, ct);
            await gitHub.SetActionsVariableAsync(repo.Owner, repo.Name, UrlVariable, baseUrl, pat, ct);
            await gitHub.SetActionsVariableAsync(repo.Owner, repo.Name, ProductIdVariable,
                product.Id.ToString(CultureInfo.InvariantCulture), pat, ct);

            status.ClearActionsSyncRetry();
            await StampReleaseSyncAsync(db, product, hash, ct);
            logger.LogInformation(
                "Synced the release configuration of product {Product} to GitHub Actions config of {Repo}",
                product.Name, repo.FullName);
            await audit.RecordAsync(AuditCategory, ReleaseAuditAction, repo.FullName,
                $"pushed the {UrlVariable}/{ProductIdVariable} variables and the {TokenSecret} secret "
                + $"of product '{product.Name}' to GitHub Actions", ct: ct);
        } catch (HttpRequestException ex) {
            await RecordReleaseFailureAsync(
                db, repo, product, status, Explain(ex, ReleaseFeature), deferRetry: true, ct);
        }
    }

    /// <summary>
    /// Writes the "these values are at GitHub" stamp onto the product, retrying once against a freshly
    /// reloaded row when a concurrent product edit invalidated the <c>xmin</c> this pass read with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The retry is not cosmetic. By the time this runs the variables and the sealed secret are
    /// <em>already at GitHub</em>; only the record of that failed. Leaving it to the next pass means
    /// re-sealing and re-pushing three values over the network to write a row this pass could have
    /// written, and — because the push is what the shared defer rate-limits — doing it minutes later.
    /// </para>
    /// <para>
    /// Re-applied after the reload rather than merged: the reload overwrites every property with what
    /// the database holds, so the three stamp fields have to be set again. Stamping the hash this pass
    /// pushed is right even when the concurrent edit rotated the token — the hash describes what went
    /// out, and the next pass computes it from the *current* token, sees the difference and re-pushes.
    /// </para>
    /// <para>
    /// The same holds for the edit that <em>disabled</em> the sync: the stamp lands on a product with
    /// <see cref="Product.SyncReleaseSecrets"/> now false, which is invisible — <c>ci.getProductCi</c>
    /// gates the whole <c>releaseSecretsSync</c> block on that flag — and re-enabling clears the hash
    /// and the stamps anyway. Writing it is honest in any case: the values really did reach GitHub, and
    /// the debt entry above is explicit that turning the sync off does not unsend them.
    /// </para>
    /// </remarks>
    private static async Task StampReleaseSyncAsync(
        WatchtowerDbContext db, Product product, string hash, CancellationToken ct) {
        for (var attempt = 1; ; attempt++) {
            product.ActionsSyncedHash = hash;
            product.ActionsSyncedAt = DateTimeOffset.UtcNow;
            product.LastActionsSyncError = null;
            try {
                await db.SaveChangesAsync(ct);
                return;
            } catch (DbUpdateConcurrencyException) when (attempt < MaxStampAttempts) {
                await db.Entry(product).ReloadAsync(ct);
                // The concurrent write was a delete: there is no row to stamp, and the next pass will
                // not find the product either.
                if (db.Entry(product).State is EntityState.Detached) return;
            }
        }
    }

    /// <summary>
    /// Persists a standing release-sync failure on the product and audits the <em>transition</em> only —
    /// the same anti-eviction rule the registry contributor follows, and for the same reason: a PAT that
    /// will never be granted the permission re-fails every few minutes.
    /// </summary>
    /// <param name="deferRetry">
    /// True only for a failed GitHub call. See <see cref="RecordRegistryFailureAsync"/> for why a local
    /// state must not arm a timer the other contributor also waits on.
    /// </param>
    private async Task RecordReleaseFailureAsync(
        WatchtowerDbContext db, CiRepo repo, Product product, CiRepoRunnerStatus status,
        string message, bool deferRetry, CancellationToken ct) {
        var isNewFailure = product.LastActionsSyncError != message;
        product.LastActionsSyncError = message;
        if (deferRetry)
            status.DeferActionsSyncRetry();
        await db.SaveChangesAsync(ct);
        if (isNewFailure) {
            await audit.RecordAsync(AuditCategory, ReleaseAuditAction, repo.FullName,
                $"syncing the release configuration of product '{product.Name}' to GitHub Actions failed",
                success: false, error: message, ct: ct);
        }
    }

    /// <summary>
    /// Turns GitHub's 403 into the sentence that actually helps. Both contributors write secrets and
    /// variables with the same PAT, so they fail the same way and say the same thing.
    /// </summary>
    private static string Explain(HttpRequestException ex, string what) =>
        ex.Message.Contains("403", StringComparison.Ordinal)
            ? $"{ex.Message} The PAT likely lacks the repository Secrets (read and write) and "
              + $"Variables (read and write) permissions the {what} needs."
            : ex.Message;
}
