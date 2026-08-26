using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Products.Handlers;

/// <summary>
/// Edits a product. Every stack and template referencing it takes the new source at its next deploy —
/// which is the point of ADR-0026, and also its sharpest edge, so the audit row names the change
/// field by field and calls a repository move out explicitly.
/// </summary>
/// <remarks>
/// The repository URL <em>is</em> editable here: repointing a product at a moved remote is a normal
/// operation, and refusing it would only push operators into editing the database. What it is not is
/// quiet — a URL change is the one edit that can silently start deploying somebody else's code, so it
/// is the one the trail spells out.
/// </remarks>
[Handler("products.update")]
public sealed class UpdateProduct(
    WatchtowerDbContext db, ProductCatalog catalog, CiRunnerOrchestrator orchestrator,
    AuditLog audit, ICurrentUser currentUser)
    : IHandler<UpdateProduct.Command, Result<UpdateProduct.Response>> {
    /// <param name="ReleaseMode">
    /// <c>"git"</c> or <c>"releases"</c>, or null to leave it as it is — the operator's manual override
    /// of the switch the first release flips automatically (ADR-0026 decision 5).
    /// </param>
    /// <param name="RetainReleases">
    /// How many releases the post-create pruning pass keeps, or null to leave it as it is. Clamped to
    /// <see cref="ReleasePruner.MinRetainReleases"/>…<see cref="ReleasePruner.MaxRetainReleases"/> rather
    /// than refused: the pruner clamps what it reads anyway (invariant 15), so refusing here would only
    /// mean the stored value and the enforced value could disagree.
    /// </param>
    public sealed record Command(
        int Id,
        string Name,
        string RepositoryUrl,
        string ComposeFilePath,
        string DefaultBranch,
        string? Description = null,
        int? CredentialId = null,
        string? ReleaseMode = null,
        int? RetainReleases = null);

    public sealed record Response(ProductDto Product);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        if (ProductMapping.Validate(
                command.Name, command.RepositoryUrl, command.ComposeFilePath, command.DefaultBranch,
                out var name, out var repositoryUrl, out var composeFilePath, out var defaultBranch)
            is { } invalid) {
            return AppError.Validation(invalid);
        }

        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == command.Id, ct);
        if (product is null)
            return AppError.NotFound($"Product {command.Id} not found.");
        if (await db.Products.AnyAsync(p => p.Name == name && p.Id != command.Id, ct))
            return AppError.Validation($"A product named '{name}' already exists.");

        // Same reason as products.create: a move onto another product's normalized source would leave
        // find-or-create with two answers and no way to pick between them.
        if (await catalog.FindConflictAsync(repositoryUrl, composeFilePath, command.Id, ct) is { } clash) {
            return AppError.Validation(
                $"Product '{clash.Name}' already deploys {clash.ComposeFilePath} from this repository. "
                + "Two products over one source would make the stack-create lookup ambiguous.");
        }

        // The mode override, validated before anything is written. Releases mode with no release would
        // leave every stack of the product resolving null and deploying branch heads while the UI
        // rendered a Version panel with nothing in it — refused rather than allowed to look broken.
        ProductReleaseMode? requestedMode = null;
        if (!string.IsNullOrWhiteSpace(command.ReleaseMode)) {
            requestedMode = ProductMapping.ParseReleaseMode(command.ReleaseMode);
            if (requestedMode is null)
                return AppError.Validation($"'{command.ReleaseMode}' is not a release mode (git, releases).");
            if (requestedMode == ProductReleaseMode.Releases
                && product.ReleaseMode != ProductReleaseMode.Releases
                && !await db.Releases.AnyAsync(r => r.ProductId == product.Id, ct)) {
                return AppError.Validation(
                    $"Product '{product.Name}' has no releases yet, so it cannot deploy them. Report one "
                    + "through the release webhook, or record one by hand, and the mode switches itself.");
            }
        }

        string? credentialName = null;
        if (command.CredentialId is { } credentialId) {
            credentialName = await db.Credentials.AsNoTracking()
                .Where(c => c.Id == credentialId).Select(c => c.Name).FirstOrDefaultAsync(ct);
            if (credentialName is null)
                return AppError.NotFound($"Credential {credentialId} not found.");
        }

        var stackCount = await db.Stacks.CountAsync(s => s.ProductId == product.Id, ct);
        var templateCount = await db.StackTemplates.CountAsync(t => t.ProductId == product.Id, ct);

        // Field-level diff collected before the assignments overwrite it, like ci.updateRepo.
        var description = string.IsNullOrWhiteSpace(command.Description) ? null : command.Description.Trim();
        // The credential gets its own action (docs/products/design.md §Audit) rather than a line inside
        // the update detail: "who changed the credential behind this product, and when" is the question
        // asked after a clone starts failing, and it should be answerable by filtering the trail.
        var previousCredentialId = product.CredentialId;
        var previousMode = product.ReleaseMode;
        var modeChanged = requestedMode is { } mode && mode != previousMode;
        if (modeChanged) product.ReleaseMode = requestedMode!.Value;
        var repositoryMoved = !string.Equals(product.RepositoryUrl, repositoryUrl, StringComparison.Ordinal);
        var changes = new List<string>();
        if (!string.Equals(product.Name, name, StringComparison.Ordinal))
            changes.Add($"renamedFrom={product.Name}");
        if (repositoryMoved) {
            changes.Add(
                $"REPOSITORY CHANGED {product.RepositoryUrl} → {repositoryUrl} "
                + $"({stackCount} stack(s), {templateCount} template(s) will deploy it from their next deploy)");
        }
        if (!string.Equals(product.ComposeFilePath, composeFilePath, StringComparison.Ordinal))
            changes.Add($"compose file {product.ComposeFilePath} → {composeFilePath}");
        if (!string.Equals(product.DefaultBranch, defaultBranch, StringComparison.Ordinal))
            changes.Add($"default branch {product.DefaultBranch} → {defaultBranch}");
        if (!string.Equals(product.Description, description, StringComparison.Ordinal))
            changes.Add("description edited");
        if (command.RetainReleases is { } requestedRetain) {
            var retain = ReleasePruner.Clamp(requestedRetain);
            if (retain != product.RetainReleases) {
                changes.Add($"release retention {product.RetainReleases} → {retain}"
                    + (retain != requestedRetain ? $" (clamped from {requestedRetain})" : ""));
                product.RetainReleases = retain;
            }
        }

        product.Name = name;
        product.Description = description;
        product.RepositoryUrl = repositoryUrl;
        product.ComposeFilePath = composeFilePath;
        product.DefaultBranch = defaultBranch;
        product.CredentialId = command.CredentialId;
        if (repositoryMoved && product.CiRepoId is not null) {
            // The CI link is a cached answer to "which GitHub repo is this?", and the question just got
            // a new answer. Clearing rather than re-resolving here keeps one resolution path: the next
            // CI read parses the new URL and records what it finds (ADR-0026 decision 7). The CiRepo row
            // itself is untouched — other products may still deploy it, and its runners keep running.
            product.CiRepoId = null;
            changes.Add("CI repository link cleared (re-resolved from the new URL on the next CI read)");
        }
        // A repository move takes the release-secret sync with it. The sync writes into *a* repository's
        // Actions config, and this product no longer names the one its token is sitting in — leaving the
        // switch on would keep a "synced" badge over a repo nothing pushes to any more, and would also
        // leave the flag standing while the FK the filtered unique index constrains is null. Re-enabling
        // it on the CI tab re-probes the new repository's PAT, which is the check that has to be redone.
        var syncTurnedOff = repositoryMoved && product.SyncReleaseSecrets;
        if (syncTurnedOff) {
            product.SyncReleaseSecrets = false;
            product.ActionsSyncedHash = null;
            product.ActionsSyncedAt = null;
            product.LastActionsSyncError = null;
            changes.Add("release secret sync turned off (the repository moved; re-enable it on the CI tab)");
        }
        // Save-to-retry for everything else: the durable failures this sync records are about the
        // instance and the product (an unset Watchtower:PublicBaseUrl, a missing token), so a product
        // save is the operator saying "I fixed it" — the same reading ci.updateRepo gives a save.
        // The hash goes with the error, not just the error: a standing failure whose hash still matches
        // the current values means the push itself failed, and clearing the message alone would satisfy
        // the "unchanged, nothing to do" guard and quietly stop retrying.
        var retrySync = !syncTurnedOff && product.SyncReleaseSecrets && product.LastActionsSyncError is not null;
        if (retrySync) {
            product.ActionsSyncedHash = null;
            product.ActionsSyncedAt = null;
            product.LastActionsSyncError = null;
        }
        await db.SaveChangesAsync(ct);
        if (retrySync && product.CiRepoId is { } syncRepoId) {
            orchestrator.ClearActionsSyncBackoff(syncRepoId);
            orchestrator.RequestReconcile();
        }

        var actor = await audit.ActorAsync(currentUser, ct);
        if (changes.Count > 0) {
            await audit.RecordAsync(
                ProductMapping.AuditCategory, "product.update", product.Name, string.Join("; ", changes),
                actor: actor, ct: ct);
        }
        if (previousCredentialId != command.CredentialId) {
            await audit.RecordAsync(
                ProductMapping.AuditCategory, "product.credential.change", product.Name,
                $"credential {Describe(previousCredentialId)} → {credentialName ?? "none"}",
                actor: actor, ct: ct);
        }
        // Its own action, and the same one the automatic first-release flip records, so "when did this
        // product change how it updates, and who did it" is one filter — whichever end moved it.
        if (modeChanged) {
            await audit.RecordAsync(
                ProductMapping.AuditCategory, ReleaseIntakeService.ModeChangeAction, product.Name,
                $"{previousMode} → {product.ReleaseMode} ({stackCount} stack(s) affected from their "
                + "next deploy)",
                actor: actor, ct: ct);
        }

        return new Response(ProductMapping.ToDto(product, credentialName, stackCount, templateCount));
    }

    /// <summary>The previous credential by id — its name may already be gone, the id never is.</summary>
    private static string Describe(int? credentialId) =>
        credentialId is { } id ? $"#{id}" : "none";
}
