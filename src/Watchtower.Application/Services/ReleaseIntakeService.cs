using Microsoft.EntityFrameworkCore;
using Npgsql;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>How a release intake attempt ended. The transports map these onto their own vocabularies.</summary>
public enum ReleaseIntakeStatus {
    /// <summary>A new release was recorded.</summary>
    Created,

    /// <summary>A release with this fingerprint already existed; nothing was written.</summary>
    Replayed,

    /// <summary>The product does not exist.</summary>
    ProductNotFound,

    /// <summary>The request could not be accepted as written — the message says why.</summary>
    Invalid,

    /// <summary>The version is already taken by a release with a different fingerprint.</summary>
    VersionConflict,

    /// <summary>A registry could not be reached in time; the same call may succeed later.</summary>
    RegistryUnavailable,
}

/// <summary>What a caller asks release intake to record.</summary>
/// <param name="ProductId">The product this is a build of.</param>
/// <param name="Images">
/// Image references, each <c>repo:tag</c> or <c>repo@sha256:…</c>. Tags are resolved to digests at
/// intake; digests pass through.
/// </param>
/// <param name="CreatedVia"><see cref="Release.ViaWebhook"/> or <see cref="Release.ViaManual"/>.</param>
/// <param name="CommitSha">The 40-hex commit, or null for a release that records only images.</param>
/// <param name="Branch">
/// The branch the build came from. Null means "the product's default", which is what the manual path
/// passes; a non-null value that is not the product default is refused (see the remarks on
/// <see cref="ReleaseIntakeService"/>).
/// </param>
/// <param name="Version">The display label; null defaults to the short commit SHA.</param>
/// <param name="RunUrl">Link back to the CI run, when the reporter supplied one.</param>
/// <param name="Notes">Free-text notes.</param>
/// <param name="Actor">Audit actor — an operator for the manual path, null for the webhook.</param>
/// <param name="CallerIp">Caller address recorded in the audit detail; null for the manual path.</param>
public sealed record ReleaseIntakeRequest(
    int ProductId,
    IReadOnlyList<string> Images,
    string CreatedVia,
    string? CommitSha = null,
    string? Branch = null,
    string? Version = null,
    string? RunUrl = null,
    string? Notes = null,
    string? Actor = null,
    string? CallerIp = null);

/// <summary>
/// The outcome of an intake attempt. <see cref="Release"/> and <see cref="ProductName"/> are set for
/// <see cref="ReleaseIntakeStatus.Created"/> and <see cref="ReleaseIntakeStatus.Replayed"/> — the two
/// statuses that name an existing release — and <see cref="Error"/> for the ones that refuse.
/// </summary>
/// <remarks>
/// <see cref="ProductName"/> rides along because intake has already read it and a freshly inserted
/// release carries no loaded <c>Product</c> navigation, so every caller that renders the release would
/// otherwise repeat the same lookup.
/// </remarks>
/// <param name="StacksEnqueued">
/// How many stacks the caller's roll-out hook enqueued, or 0 when there was none and for every status
/// other than <see cref="ReleaseIntakeStatus.Created"/> — a replay deploys nothing.
/// </param>
public sealed record ReleaseIntakeResult(
    ReleaseIntakeStatus Status, Release? Release, string? Error, string? ProductName = null,
    int StacksEnqueued = 0) {
    public bool IsAccepted => Status is ReleaseIntakeStatus.Created or ReleaseIntakeStatus.Replayed;
}

/// <summary>
/// The whole release intake pipeline: validate, resolve tags to digests, fingerprint, and record
/// (ADR-0026, docs/products/design.md §Release intake). Shared by the two ways a release arrives — the
/// product webhook and <c>products.createRelease</c> — so the two cannot drift on what a release
/// <em>is</em>. The webhook adds authentication and rate limiting on top; the handler adds an actor to
/// the audit row.
/// </summary>
/// <remarks>
/// <para>
/// <b>The branch check is a safety property, not a formality.</b> A workflow that also runs on pull
/// requests would otherwise publish a feature-branch build to every stack of the product. It is
/// checked against the <em>product's</em> default branch: per-stack and per-template overrides say
/// which branch an individual deployment tracks, which is a different question from which branch this
/// product's releases are built from.
/// </para>
/// <para>
/// <b>Digest resolution is Watchtower's job.</b> Making the workflow wire each image's
/// <c>steps.build.outputs.digest</c> through is the step people get wrong; resolving the tag the
/// workflow just pushed is one HEAD request and the answer is pinned forever. Credentials come from
/// the resolved registry view matched on the image's registry host — never from the product's git
/// credential, which is a different secret for a different service.
/// </para>
/// <para>
/// <b>An unknown registry host is refused</b> (the threat model for a leaked token): a release may only
/// pin images from a registry this instance actually knows about, or from Docker Hub, so the worst a
/// stolen token buys is a redeploy of images that were already yours.
/// </para>
/// <para>
/// <b>Nothing here deploys anything</b>, still. What a <em>created</em> release does have is one side
/// effect: a product in <see cref="ProductReleaseMode.Git"/> flips to
/// <see cref="ProductReleaseMode.Releases"/> in the same <c>SaveChanges</c> as the insert (ADR-0026
/// decision 5), which is the moment its stacks stop deploying branch heads. Fanning the release out to
/// them is the caller's job — the webhook and <c>products.deployRelease</c> both go through
/// <see cref="ReleaseRolloutService"/> — because a rollout is a transport-level decision (a replayed
/// call must not re-deploy anything) and because it must happen after the insert commits, not inside it.
/// </para>
/// <para>
/// <b>Accepted:</b> the flip carries <c>Product</c>'s <c>xmin</c> concurrency token, so a product edit
/// landing in the microseconds between the read and the save makes that one call fail with a
/// concurrency exception and write nothing. It is retryable and self-correcting (the retry finds the
/// mode already flipped and skips it), it can only happen on the single call in a product's life that
/// flips the mode, and the alternative — an unconditional second statement — would trade it for a
/// window in which a release exists while the product still deploys branch heads.
/// </para>
/// </remarks>
public class ReleaseIntakeService(
    WatchtowerDbContext db,
    RegistryAuthBuilder registries,
    IReleaseDigestResolver digests,
    AuditLog audit,
    TimeProvider time) {
    /// <summary>Most images one release may pin. The webhook rejects a larger payload before parsing it.</summary>
    public const int MaxImages = 20;

    /// <summary>Longest accepted version label — a display value, not a document.</summary>
    public const int MaxVersionLength = 100;

    /// <summary>
    /// Total wall-clock budget for resolving every tag in one request. Registry HEADs run in parallel,
    /// so this is the whole step rather than per image: a CI job is waiting on the answer, and a
    /// 503 it can retry is a better answer than a request that hangs.
    /// </summary>
    public static readonly TimeSpan ResolutionBudget = TimeSpan.FromSeconds(10);

    /// <summary>Audit action recorded for every accepted new release.</summary>
    public const string PublishAction = "release.publish";

    /// <summary>
    /// Audit action recorded whenever <see cref="Product.ReleaseMode"/> changes — actor-less when the
    /// first release flipped it, attributed when an operator did through <c>products.update</c>.
    /// </summary>
    public const string ModeChangeAction = "release.mode.change";

    /// <summary>
    /// Savepoint the speculative insert rolls back to when it loses the race to an identical concurrent
    /// call. A savepoint rather than a bare catch because a failed statement poisons the surrounding
    /// PostgreSQL transaction when the caller has one — the re-read would fail too
    /// (the <see cref="ProductCatalog"/> precedent).
    /// </summary>
    private const string InsertSavepoint = "wt_release_intake";

    /// <summary>
    /// Records a release, or explains why it cannot. Never throws for a caller error: every refusal is
    /// a <see cref="ReleaseIntakeResult"/> the transport maps to its own status.
    /// </summary>
    public Task<ReleaseIntakeResult> PublishAsync(ReleaseIntakeRequest request, CancellationToken ct) =>
        PublishAsync(request, onCreated: null, ct);

    /// <inheritdoc cref="PublishAsync(ReleaseIntakeRequest, CancellationToken)"/>
    /// <param name="request">What to record.</param>
    /// <param name="onCreated">
    /// Optional roll-out hook, invoked once for a <em>newly created</em> release and never for a replay
    /// or a refusal, returning how many stacks it enqueued. It runs after the insert and before the
    /// audit row, so the trail says what the release actually reached rather than what it was expected
    /// to.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// <b>A caller that holds an open transaction must not supply <paramref name="onCreated"/>.</b> The
    /// hook runs after <c>SaveChanges</c>, which commits only when nothing else owns a transaction on
    /// this context; inside one, the release is still uncommitted when the hook fires. The enqueued
    /// deploys resolve their release when they run, on other threads and through other connections, so
    /// they would not see it — each would resolve the previous release, or none, and the fan-out would
    /// deploy the wrong thing to the whole fleet.
    /// <para>
    /// The release webhook is a safe caller: it is a minimal-API endpoint that opens no transaction of
    /// its own, so its <c>SaveChanges</c> is the commit. Several handlers do open one — which is why
    /// <c>products.createRelease</c> records and stops, and <c>products.deployRelease</c> is the
    /// separate, explicit way to roll a release out.
    /// </para>
    /// </remarks>
    public async Task<ReleaseIntakeResult> PublishAsync(
        ReleaseIntakeRequest request,
        Func<Release, CancellationToken, Task<int>>? onCreated,
        CancellationToken ct) {
        var product = await db.Products.AsNoTracking()
            .Where(p => p.Id == request.ProductId)
            .Select(p => new { p.Id, p.Name, p.DefaultBranch, p.ReleaseMode })
            .FirstOrDefaultAsync(ct);
        if (product is null)
            return Refuse(ReleaseIntakeStatus.ProductNotFound, $"Product {request.ProductId} not found.");

        // ── the request, as written ──────────────────────────────────────────
        var commit = string.IsNullOrWhiteSpace(request.CommitSha) ? null : request.CommitSha.Trim();
        if (commit is not null && !ReleaseFingerprint.IsCommitSha(commit))
            return Invalid("'commit' must be a full 40-character hexadecimal commit SHA.");
        commit = commit?.ToLowerInvariant();

        var branch = string.IsNullOrWhiteSpace(request.Branch) ? product.DefaultBranch : request.Branch.Trim();
        if (!string.Equals(branch, product.DefaultBranch, StringComparison.Ordinal)) {
            return Invalid(
                $"This build is from branch '{branch}', but product '{product.Name}' publishes releases "
                + $"from '{product.DefaultBranch}'. Change the product's default branch, or report "
                + "releases only from it.");
        }

        if (request.Images.Count == 0) return Invalid("At least one image is required.");
        if (request.Images.Count > MaxImages)
            return Invalid($"A release may pin at most {MaxImages} images; {request.Images.Count} were sent.");

        var version = string.IsNullOrWhiteSpace(request.Version) ? null : request.Version.Trim();
        if (version is null) {
            if (commit is null)
                return Invalid("A version is required when the release records no commit.");
            version = ReleaseFingerprint.ShortSha(commit);
        }
        if (version.Length > MaxVersionLength)
            return Invalid($"A version may be at most {MaxVersionLength} characters.");

        // ── images: parse, gate on the registry, resolve the tags ────────────
        var known = KnownRegistryHosts();
        var parsed = new List<(string Raw, ImageRef Ref)>(request.Images.Count);
        foreach (var image in request.Images) {
            if (!ImageRef.TryParse(image, out var reference))
                return Invalid(DescribeUnparseable(image));
            if (!IsAllowedRegistry(reference.Registry, known)) {
                return Invalid(
                    $"'{image}' is on registry '{reference.Registry}', which is not configured in "
                    + "Watchtower. Add it under Registries, or report an image from a known registry.");
            }
            parsed.Add((image.Trim(), reference));
        }

        // Before resolution, not after: the repository is known at parse time, and asking a registry
        // about images that are going to be refused anyway wastes the budget the caller is waiting on.
        // The unique index on (release_id, repository) would refuse this too; refusing it here names
        // the repository instead of surfacing a constraint violation.
        var duplicate = parsed.GroupBy(p => p.Ref.CanonicalRepository, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            return Invalid($"Repository '{duplicate.Key}' is listed more than once.");

        var resolved = await ResolveAsync(parsed, known, ct);
        if (resolved.Error is { } resolveError) return resolveError;

        var images = resolved.Images!;
        var fingerprint = ReleaseFingerprint.Compute(commit, images.Select(i => (i.Repository, i.Digest)));

        // ── idempotency, then the write ──────────────────────────────────────
        var (replay, versionTaken) = await PrecheckAsync(product.Id, fingerprint, version, ct);
        if (replay is not null)
            return new ReleaseIntakeResult(ReleaseIntakeStatus.Replayed, replay, null, product.Name);
        if (versionTaken) return Refuse(ReleaseIntakeStatus.VersionConflict, VersionTaken(product.Name, version));

        var release = new Release {
            ProductId = product.Id,
            Version = version,
            CommitSha = commit,
            Branch = branch,
            Fingerprint = fingerprint,
            SourceRunUrl = Trimmed(request.RunUrl),
            Notes = Trimmed(request.Notes),
            CreatedVia = request.CreatedVia,
            CreatedAt = time.GetUtcNow(),
            Images = [.. images.Select(i => new ReleaseImage {
                Repository = i.Repository, Tag = i.Tag, Digest = i.Digest,
            })],
        };

        // The mode flip rides in the same SaveChanges as the insert (ADR-0026 decision 5): a product
        // whose first release exists but that is still in Git mode would keep deploying branch heads
        // while its Releases tab said otherwise, and there is no sequence of two writes that cannot be
        // interrupted between them. Tracked, not ExecuteUpdate, precisely so it participates.
        Product? flipped = null;
        if (product.ReleaseMode == ProductReleaseMode.Git) {
            flipped = await db.Products.FirstAsync(p => p.Id == product.Id, ct);
            flipped.ReleaseMode = ProductReleaseMode.Releases;
        }

        var transaction = db.Database.CurrentTransaction;
        if (transaction is not null) await transaction.CreateSavepointAsync(InsertSavepoint, ct);
        try {
            db.Releases.Add(release);
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.ReleaseSavepointAsync(InsertSavepoint, ct);
        } catch (DbUpdateException ex) when (IsUniqueViolation(ex)) {
            // Two identical calls raced — the common case is a CI job whose curl was retried — or two
            // different builds picked the same version. Both surface here; which one it was is answered
            // by looking for the fingerprint, not by parsing the constraint name.
            if (transaction is not null) await transaction.RollbackToSavepointAsync(InsertSavepoint, ct);
            db.Entry(release).State = EntityState.Detached;
            foreach (var image in release.Images) db.Entry(image).State = EntityState.Detached;
            // The flip lost with the insert — at the database because the whole statement batch rolled
            // back, and here because a change tracker still holding it Modified would smuggle it into
            // whatever the caller saves next. Restored rather than reloaded: the value it had is known.
            if (flipped is not null) {
                flipped.ReleaseMode = ProductReleaseMode.Git;
                db.Entry(flipped).State = EntityState.Unchanged;
            }

            return await FindByFingerprintAsync(product.Id, fingerprint, ct) is { } winner
                ? new ReleaseIntakeResult(ReleaseIntakeStatus.Replayed, winner, null, product.Name)
                : Refuse(ReleaseIntakeStatus.VersionConflict, VersionTaken(product.Name, version));
        }

        // The roll-out, before the audit row so the trail can say what it reached. The release is
        // committed by now on every path allowed to supply a hook — see the remarks on this overload.
        var stacksEnqueued = onCreated is null ? 0 : await onCreated(release, ct);

        // Actor-less for the webhook — nobody is signed in — with the caller address in the detail, so a
        // release published by a stolen token is still attributable to where it came from.
        var origin = request.CallerIp is { Length: > 0 } ip ? $" from {ip}" : string.Empty;
        await audit.RecordAsync(
            ProductCatalog.AuditCategory, PublishAction, $"{product.Name}/{version}",
            $"source {request.CreatedVia}; commit {ReleaseFingerprint.DescribeCommit(commit)}; "
            + $"{images.Count} image(s); {stacksEnqueued} stack(s) enqueued{origin}",
            actor: request.Actor, ct: ct);

        // Its own row rather than a clause inside the publish detail: "when did this product start
        // deploying releases, and what caused it" is the question asked after the first release moved a
        // fleet off branch heads, and it should be answerable by filtering the trail on one action.
        if (flipped is not null) {
            // Almost always the product's first release, but not necessarily: an operator can revert a
            // product to Git mode, and the next release flips it forward again. One extra indexed read
            // on a path that runs once per product (or once per revert) buys a detail line that is true
            // in both cases instead of one that quietly lies in the second.
            var isFirst = !await db.Releases.AsNoTracking()
                .AnyAsync(r => r.ProductId == product.Id && r.Id != release.Id, ct);
            await audit.RecordAsync(
                ProductCatalog.AuditCategory, ModeChangeAction, product.Name,
                $"{(isFirst ? "first release" : "release")} '{version}' flipped Git → Releases",
                actor: request.Actor, ct: ct);
        }

        return new ReleaseIntakeResult(
            ReleaseIntakeStatus.Created, release, null, product.Name, stacksEnqueued);
    }

    /// <summary>One image as it will be stored, after any tag lookup.</summary>
    private sealed record ResolvedImage(string Repository, string? Tag, string Digest);

    /// <summary>Either the resolved images, or the refusal to answer the caller with.</summary>
    private sealed record Resolution(IReadOnlyList<ResolvedImage>? Images, ReleaseIntakeResult? Error);

    /// <summary>
    /// Turns every reference into a <c>(repository, digest)</c> pair: digest references pass through
    /// untouched, tags are looked up in parallel under one budget.
    /// </summary>
    private async Task<Resolution> ResolveAsync(
        IReadOnlyList<(string Raw, ImageRef Ref)> parsed,
        IReadOnlyDictionary<string, ResolvedRegistry> known,
        CancellationToken ct) {
        var results = new ResolvedImage?[parsed.Count];
        var lookups = new List<Task<(int Index, ReleaseDigestResult Result)>>();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);

        for (var i = 0; i < parsed.Count; i++) {
            var (raw, image) = parsed[i];
            if (image.Digest is { } digest) {
                results[i] = new ResolvedImage(image.CanonicalRepository, image.Tag, digest);
                continue;
            }
            var index = i;
            var credential = known.GetValueOrDefault(image.Registry);
            lookups.Add(ResolveOneAsync(index, raw, credential, budget.Token));
        }

        if (lookups.Count > 0) {
            budget.CancelAfter(ResolutionBudget);
            var answers = await Task.WhenAll(lookups);
            // A caller that hung up is not a registry problem; let it propagate as cancellation.
            ct.ThrowIfCancellationRequested();

            foreach (var (index, answer) in answers) {
                var (raw, image) = parsed[index];
                switch (answer.Status) {
                    case ReleaseDigestStatus.Resolved:
                        results[index] = new ResolvedImage(
                            image.CanonicalRepository, image.Tag ?? ImageRef.DefaultTag, answer.Digest!);
                        break;
                    case ReleaseDigestStatus.NotFound:
                        return new Resolution(null, Invalid(
                            $"The registry has no '{raw}'. Check the tag was pushed before the release "
                            + "was reported."));
                    default:
                        return new Resolution(null, Refuse(
                            ReleaseIntakeStatus.RegistryUnavailable,
                            $"Could not reach the registry for {image.CanonicalRepository} within "
                            + $"{ResolutionBudget.TotalSeconds:0} seconds. Nothing was recorded — retry."));
                }
            }
        }

        return new Resolution([.. results.Select(r => r!)], null);
    }

    /// <summary>
    /// One tag lookup, passing the reference through <em>as written</em>: the canonical form is
    /// lower-cased for comparison, and a registry that distinguishes case would not find it.
    /// </summary>
    private async Task<(int Index, ReleaseDigestResult Result)> ResolveOneAsync(
        int index, string imageReference, ResolvedRegistry? credential, CancellationToken ct) {
        var result = await digests.ResolveAsync(
            imageReference, credential?.Username, credential?.Password, ct);
        return (index, result);
    }

    /// <summary>
    /// The registries this instance knows, keyed by normalized host — see
    /// <see cref="ReleaseImageValidator.KnownHosts"/>, which the pin pre-flight shares so intake's
    /// registry gate and that pre-flight cannot disagree about which credential belongs to a host.
    /// </summary>
    private Dictionary<string, ResolvedRegistry> KnownRegistryHosts() =>
        ReleaseImageValidator.KnownHosts(registries);

    /// <summary>
    /// Docker Hub is always allowed — it is where an unqualified image lives and needs no credential to
    /// read a public repository; everything else has to be a registry this instance knows.
    /// </summary>
    private static bool IsAllowedRegistry(string host, IReadOnlyDictionary<string, ResolvedRegistry> known) =>
        string.Equals(host, ImageRef.DockerHubRegistry, StringComparison.Ordinal) || known.ContainsKey(host);

    /// <summary>
    /// The two pre-checks, as one read of the world before the insert: the release this fingerprint
    /// already names (a replay), and whether this version is taken by something else.
    /// </summary>
    /// <remarks>
    /// Both are advisory — the unique indexes are the enforcement — and both exist for the message. They
    /// are one virtual method because a concurrent writer is invisible to <em>both</em> of them at once:
    /// overriding this is how a test describes the losing side of two identical simultaneous reports,
    /// which is the branch that turns a routine double-submit into a 500 when it is wrong, and which no
    /// amount of timing can produce reliably.
    /// </remarks>
    protected virtual async Task<(Release? Replay, bool VersionTaken)> PrecheckAsync(
        int productId, string fingerprint, string version, CancellationToken ct) {
        if (await FindByFingerprintAsync(productId, fingerprint, ct) is { } replay) return (replay, false);
        var taken = await db.Releases.AsNoTracking()
            .AnyAsync(r => r.ProductId == productId && r.Version == version, ct);
        return (null, taken);
    }

    /// <summary>The release with this fingerprint, images included, or null.</summary>
    private Task<Release?> FindByFingerprintAsync(
        int productId, string fingerprint, CancellationToken ct) =>
        db.Releases.AsNoTracking()
            .Include(r => r.Images)
            .FirstOrDefaultAsync(r => r.ProductId == productId && r.Fingerprint == fingerprint, ct);

    /// <summary>
    /// Why a reference did not parse. The one case worth naming is an upper-case digest: OCI encodes
    /// digests in lower-case hex, so <c>@sha256:AB12…</c> is rejected — and a caller told only "not a
    /// valid image reference" about a string that plainly looks like one goes looking in the wrong
    /// place. Everything else is genuinely malformed and gets the general answer.
    /// </summary>
    private static string DescribeUnparseable(string image) {
        var at = image.LastIndexOf('@');
        if (at >= 0) {
            var digest = image[(at + 1)..].Trim();
            var separator = digest.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0) {
                var encoded = digest[(separator + 1)..];
                if (encoded.Length >= 32 && encoded.All(char.IsAsciiHexDigit)
                    && !encoded.Equals(encoded.ToLowerInvariant(), StringComparison.Ordinal)) {
                    return $"'{image}' has an upper-case digest — digests must be lower-case hex "
                        + $"(try '{image[..at]}@{digest.ToLowerInvariant()}').";
                }
            }
        }
        return $"'{image}' is not a valid image reference.";
    }

    /// <summary>The one refusal for a reused version, so the pre-check and the index race say the same thing.</summary>
    private static string VersionTaken(string productName, string version) =>
        $"Product '{productName}' already has a release '{version}' built from different images. "
        + "Report it with a different version.";

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ReleaseIntakeResult Invalid(string error) =>
        new(ReleaseIntakeStatus.Invalid, null, error);

    private static ReleaseIntakeResult Refuse(ReleaseIntakeStatus status, string error) =>
        new(status, null, error);

    /// <summary>A write that lost a race on a unique index, as opposed to any other write failure.</summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
