using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Release intake (ADR-0026 decision 3, docs/products/design.md §Release intake): the fingerprint, the
/// branch gate, the registry gate, digest resolution and the idempotency rules — everything the product
/// webhook and <c>products.createRelease</c> share.
/// </summary>
/// <remarks>
/// The registry is stubbed (<see cref="StubDigestResolver"/>): the shipped resolver is the one part of
/// intake that leaves the machine, and none of the rules below are about HTTP. Digest references never
/// reach it at all, which is itself asserted.
/// </remarks>
public sealed class ReleaseIntakeTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Commit = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    private const string OtherCommit = "b1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    private const string ApiDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string WorkerDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    /// <summary>A digest with letters in it — the only kind upper-casing actually changes.</summary>
    private const string LetteredDigest = "sha256:abcdef1234567890abcdef1234567890abcdef1234567890abcdef1234567890";

    // ── the fingerprint ──────────────────────────────────────────────────────

    /// <summary>
    /// Two workflows listing the same images the other way round describe the same build. If the order
    /// leaked into the hash, the second report would be a second release of an identical artifact.
    /// </summary>
    [Fact]
    public void Fingerprint_IsIndependentOfTheOrderTheImagesWereReportedIn() {
        var one = ReleaseFingerprint.Compute(Commit, [
            ("ghcr.io/acme/api", ApiDigest), ("ghcr.io/acme/worker", WorkerDigest),
        ]);
        var other = ReleaseFingerprint.Compute(Commit, [
            ("ghcr.io/acme/worker", WorkerDigest), ("ghcr.io/acme/api", ApiDigest),
        ]);
        Assert.Equal(one, other);
    }

    /// <summary>
    /// The two things that make it a different build: a different commit, or a different digest for the
    /// same repository — the rebuild case a commit-keyed rule would swallow.
    /// </summary>
    [Fact]
    public void Fingerprint_ChangesWithTheCommitAndWithAnyDigest() {
        var baseline = ReleaseFingerprint.Compute(Commit, [("ghcr.io/acme/api", ApiDigest)]);

        Assert.NotEqual(baseline, ReleaseFingerprint.Compute(OtherCommit, [("ghcr.io/acme/api", ApiDigest)]));
        Assert.NotEqual(baseline, ReleaseFingerprint.Compute(Commit, [("ghcr.io/acme/api", WorkerDigest)]));
        // …and a second image is a different build too.
        Assert.NotEqual(baseline, ReleaseFingerprint.Compute(Commit, [
            ("ghcr.io/acme/api", ApiDigest), ("ghcr.io/acme/worker", WorkerDigest),
        ]));
    }

    /// <summary>Lower-case hex of a SHA-256, and the same value for the same input on every run.</summary>
    [Fact]
    public void Fingerprint_IsDeterministicLowerCaseHex() {
        var value = ReleaseFingerprint.Compute(Commit, [("ghcr.io/acme/api", ApiDigest)]);
        Assert.Equal(64, value.Length);
        Assert.Equal(value.ToLowerInvariant(), value);
        Assert.Equal(value, ReleaseFingerprint.Compute(Commit.ToUpperInvariant(), [("ghcr.io/acme/api", ApiDigest)]));
    }

    // ── resolution ───────────────────────────────────────────────────────────

    /// <summary>
    /// A digest reference is already the answer, so nothing is asked; a tag is resolved once and the
    /// digest — not the tag — is what gets stored.
    /// </summary>
    [Fact]
    public async Task Publish_PassesDigestsThroughAndResolvesOnlyTheTags() {
        var resolver = new StubDigestResolver { Digest = WorkerDigest };
        using var host = StartHost(resolver);
        var productId = await SeedProductAsync(host);

        var result = await PublishAsync(host, new ReleaseIntakeRequest(
            productId,
            [$"docker.io/acme/api@{ApiDigest}", "docker.io/acme/worker:2026.8"],
            Release.ViaWebhook,
            CommitSha: Commit,
            Branch: "main"));

        Assert.Equal(ReleaseIntakeStatus.Created, result.Status);
        Assert.Equal([("docker.io/acme/worker:2026.8", null, null)], resolver.Asked);

        var images = await ImagesAsync(host, result.Release!.Id);
        Assert.Equal(
            [("docker.io/acme/api", null, ApiDigest), ("docker.io/acme/worker", "2026.8", WorkerDigest)],
            images);
    }

    /// <summary>
    /// The threat model for a leaked token: a release may only pin images from a registry this instance
    /// knows about (or Docker Hub), so a stolen token cannot make a stack run somebody else's image.
    /// </summary>
    [Fact]
    public async Task Publish_RefusesAnImageFromARegistryTheInstanceDoesNotKnow() {
        var resolver = new StubDigestResolver { Digest = ApiDigest };
        using var host = StartHost(resolver);
        var productId = await SeedProductAsync(host);

        var result = await PublishAsync(host, Request(productId, ["registry.invalid/acme/api:1"]));

        Assert.Equal(ReleaseIntakeStatus.Invalid, result.Status);
        Assert.Contains("registry.invalid", result.Error!, StringComparison.Ordinal);
        // Refused before anything was asked of a registry, and nothing was written.
        Assert.Empty(resolver.Asked);
        Assert.Empty(await ReleasesAsync(host));
    }

    /// <summary>…and a configured registry is accepted, which is the other half of the same rule.</summary>
    [Fact]
    public async Task Publish_AcceptsAnImageFromAConfiguredRegistry() {
        var resolver = new StubDigestResolver { Digest = ApiDigest };
        using var host = StartHost(resolver);
        var productId = await SeedProductAsync(host);
        await AddRegistryAsync(host, "ghcr.io");

        var result = await PublishAsync(host, Request(productId, ["ghcr.io/acme/api:1"]));

        Assert.Equal(ReleaseIntakeStatus.Created, result.Status);
        // The registry's credential was used, not the product's git credential.
        Assert.Equal([("ghcr.io/acme/api:1", "registry-user", "registry-secret")], resolver.Asked);
    }

    /// <summary>
    /// The safety property the required branch exists for: a workflow that also runs on pull requests
    /// must not be able to publish a feature build to every stack of the product.
    /// </summary>
    [Fact]
    public async Task Publish_RefusesABranchThatIsNotTheProductsDefault() {
        using var host = StartHost(new StubDigestResolver { Digest = ApiDigest });
        var productId = await SeedProductAsync(host);

        var result = await PublishAsync(host, Request(productId, [$"docker.io/acme/api@{ApiDigest}"], branch: "feature/x"));

        Assert.Equal(ReleaseIntakeStatus.Invalid, result.Status);
        // Names both branches, so the reader knows which end to change.
        Assert.Contains("feature/x", result.Error!, StringComparison.Ordinal);
        Assert.Contains("main", result.Error!, StringComparison.Ordinal);
        Assert.Empty(await ReleasesAsync(host));
    }

    /// <summary>A tag the registry does not have is the reporter's mistake, and the message names it.</summary>
    [Fact]
    public async Task Publish_RefusesATagTheRegistryDoesNotHave() {
        using var host = StartHost(new StubDigestResolver { Answer = ReleaseDigestResult.NotFound });
        var productId = await SeedProductAsync(host);

        var result = await PublishAsync(host, Request(productId, ["docker.io/acme/api:missing"]));

        Assert.Equal(ReleaseIntakeStatus.Invalid, result.Status);
        Assert.Contains("docker.io/acme/api:missing", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A registry that cannot be reached is not the caller's fault: the transport answers 503 with a
    /// Retry-After, and nothing is half-recorded.
    /// </summary>
    [Fact]
    public async Task Publish_ReportsAnUnreachableRegistrySeparatelyFromAMissingTag() {
        using var host = StartHost(new StubDigestResolver { Answer = ReleaseDigestResult.Unavailable });
        var productId = await SeedProductAsync(host);

        var result = await PublishAsync(host, Request(productId, ["docker.io/acme/api:1"]));

        Assert.Equal(ReleaseIntakeStatus.RegistryUnavailable, result.Status);
        Assert.Empty(await ReleasesAsync(host));
    }

    // ── identity and idempotency ─────────────────────────────────────────────

    /// <summary>The version an unnamed release gets: git's own short form of the commit.</summary>
    [Fact]
    public async Task Publish_DefaultsTheVersionToTheShortCommit() {
        using var host = StartHost(new StubDigestResolver { Digest = ApiDigest });
        var productId = await SeedProductAsync(host);

        var result = await PublishAsync(host, Request(productId, [$"docker.io/acme/api@{ApiDigest}"]));

        Assert.Equal(ReleaseIntakeStatus.Created, result.Status);
        Assert.Equal(Commit[..7], result.Release!.Version);
    }

    /// <summary>
    /// A retried <c>curl</c>: the identical payload answers with the release that already exists, writes
    /// nothing, and audits nothing a second time.
    /// </summary>
    [Fact]
    public async Task Publish_AnswersAReplayWithTheExistingReleaseAndWritesNothing() {
        using var host = StartHost(new StubDigestResolver { Digest = ApiDigest });
        var productId = await SeedProductAsync(host);
        var request = Request(productId, [$"docker.io/acme/api@{ApiDigest}"], version: "1.4.0");

        var first = await PublishAsync(host, request);
        var replay = await PublishAsync(host, request);

        Assert.Equal(ReleaseIntakeStatus.Created, first.Status);
        Assert.Equal(ReleaseIntakeStatus.Replayed, replay.Status);
        Assert.Equal(first.Release!.Id, replay.Release!.Id);
        Assert.Single(await ReleasesAsync(host));
        // One release, one audit row — a replay is not an event.
        var audits = await AuditAsync(host, ReleaseIntakeService.PublishAction);
        var audit = Assert.Single(audits);
        Assert.Equal("shop/1.4.0", audit.Target);
        Assert.Null(audit.Actor);
        Assert.Contains("source webhook", audit.Detail!, StringComparison.Ordinal);
        Assert.Contains(Commit[..7], audit.Detail!, StringComparison.Ordinal);
        Assert.Contains("1 image(s)", audit.Detail!, StringComparison.Ordinal);
        Assert.Contains("from 203.0.113.7", audit.Detail!, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same version for a genuinely different build is refused: the label is what an operator picks
    /// a release by, and two builds behind one label is the state nothing can recover from.
    /// </summary>
    [Fact]
    public async Task Publish_RefusesAVersionAlreadyUsedByADifferentBuild() {
        using var host = StartHost(new StubDigestResolver { Digest = ApiDigest });
        var productId = await SeedProductAsync(host);
        await PublishAsync(host, Request(productId, [$"docker.io/acme/api@{ApiDigest}"], version: "1.4.0"));

        var clash = await PublishAsync(
            host, Request(productId, [$"docker.io/acme/api@{WorkerDigest}"], version: "1.4.0"));

        Assert.Equal(ReleaseIntakeStatus.VersionConflict, clash.Status);
        Assert.Contains("1.4.0", clash.Error!, StringComparison.Ordinal);
        Assert.Single(await ReleasesAsync(host));
    }

    /// <summary>
    /// A rebuild of the same commit onto new base layers is a new release — the case the fingerprint
    /// rule exists for, and the one a commit-keyed rule would have swallowed.
    /// </summary>
    [Fact]
    public async Task Publish_RecordsARebuildOfTheSameCommitAsANewRelease() {
        using var host = StartHost(new StubDigestResolver { Digest = ApiDigest });
        var productId = await SeedProductAsync(host);
        await PublishAsync(host, Request(productId, [$"docker.io/acme/api@{ApiDigest}"], version: "1.4.0"));

        var rebuild = await PublishAsync(
            host, Request(productId, [$"docker.io/acme/api@{WorkerDigest}"], version: "1.4.1"));

        Assert.Equal(ReleaseIntakeStatus.Created, rebuild.Status);
        Assert.Equal(2, (await ReleasesAsync(host)).Count);
    }

    /// <summary>
    /// Two identical reports arriving at once: the pre-check misses, the unique index catches it, and
    /// the loser is answered with the winner's release instead of a 500.
    /// </summary>
    /// <remarks>
    /// The interleave is described rather than raced — <see cref="BlindIntakeService"/> makes the
    /// pre-check answer "nothing here" once, which is exactly what the losing request sees when the
    /// winner commits between its read and its insert.
    /// </remarks>
    [Fact]
    public async Task Publish_CollapsesTwoIdenticalReportsOntoOneRelease() {
        using var host = StartHost(new StubDigestResolver { Digest = ApiDigest });
        var productId = await SeedProductAsync(host);
        var request = Request(productId, [$"docker.io/acme/api@{ApiDigest}"], version: "1.4.0");

        var winner = await PublishAsync(host, request);

        await using var scope = host.Services.CreateAsyncScope();
        var blind = ActivatorUtilities.CreateInstance<BlindIntakeService>(scope.ServiceProvider);
        var loser = await blind.PublishAsync(request, Ct);

        Assert.Equal(ReleaseIntakeStatus.Replayed, loser.Status);
        Assert.Equal(winner.Release!.Id, loser.Release!.Id);
        Assert.Single(await ReleasesAsync(host));
        // …and the loser wrote nothing, including no second image row.
        Assert.Single(await ImagesAsync(host, winner.Release.Id));
    }

    /// <summary>
    /// The other arm of the same race: the pre-checks miss, and what the index refuses is the
    /// <em>version</em> rather than the fingerprint — two different builds that picked one label. The
    /// caller gets the conflict, not a 500.
    /// </summary>
    [Fact]
    public async Task Publish_AnswersTheLostVersionRaceWithAConflict() {
        using var host = StartHost(new StubDigestResolver { Digest = ApiDigest });
        var productId = await SeedProductAsync(host);
        await PublishAsync(host, Request(productId, [$"docker.io/acme/api@{ApiDigest}"], version: "1.4.0"));

        await using var scope = host.Services.CreateAsyncScope();
        var blind = ActivatorUtilities.CreateInstance<BlindIntakeService>(scope.ServiceProvider);
        // Same version, different images — so the fingerprint index has nothing to say and the version
        // one does.
        var loser = await blind.PublishAsync(
            Request(productId, [$"docker.io/acme/api@{WorkerDigest}"], version: "1.4.0"), Ct);

        Assert.Equal(ReleaseIntakeStatus.VersionConflict, loser.Status);
        Assert.Contains("1.4.0", loser.Error!, StringComparison.Ordinal);
        Assert.Single(await ReleasesAsync(host));
    }

    /// <summary>Malformed input is refused before anything else happens.</summary>
    [Theory]
    [InlineData("not-a-sha", "commit")]
    [InlineData("a1b2c3d", "commit")]
    public async Task Publish_RefusesACommitThatIsNotAFullSha(string commit, string expected) {
        using var host = StartHost(new StubDigestResolver { Digest = ApiDigest });
        var productId = await SeedProductAsync(host);

        var result = await PublishAsync(host, new ReleaseIntakeRequest(
            productId, [$"docker.io/acme/api@{ApiDigest}"], Release.ViaWebhook,
            CommitSha: commit, Branch: "main"));

        Assert.Equal(ReleaseIntakeStatus.Invalid, result.Status);
        Assert.Contains(expected, result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// One repository, two digests, is not a build anybody can deploy — and it is refused before any
    /// registry is asked, because the repository is known at parse time and the caller is waiting on
    /// the resolution budget.
    /// </summary>
    [Fact]
    public async Task Publish_RefusesTheSameRepositoryTwice_WithoutAskingARegistry() {
        var resolver = new StubDigestResolver { Digest = ApiDigest };
        using var host = StartHost(resolver);
        var productId = await SeedProductAsync(host);

        var result = await PublishAsync(host, Request(
            productId, [$"docker.io/acme/api@{ApiDigest}", "docker.io/acme/api:2026.8"]));

        Assert.Equal(ReleaseIntakeStatus.Invalid, result.Status);
        Assert.Contains("docker.io/acme/api", result.Error!, StringComparison.Ordinal);
        Assert.Empty(resolver.Asked);
    }

    /// <summary>
    /// An upper-case digest is the one malformed reference worth naming: it looks perfectly valid, and
    /// "not a valid image reference" would send the reader looking in the wrong place. A digest with
    /// letters in it, deliberately — an all-numeric one is unchanged by upper-casing and would pass.
    /// </summary>
    [Fact]
    public async Task Publish_NamesTheRuleWhenADigestIsUpperCase() {
        using var host = StartHost(new StubDigestResolver { Digest = ApiDigest });
        var productId = await SeedProductAsync(host);

        var result = await PublishAsync(
            host, Request(productId, [$"docker.io/acme/api@{LetteredDigest.ToUpperInvariant()}"]));

        Assert.Equal(ReleaseIntakeStatus.Invalid, result.Status);
        Assert.Contains("lower-case hex", result.Error!, StringComparison.Ordinal);
        // …and it hands back the reference that would have worked.
        Assert.Contains(LetteredDigest, result.Error!, StringComparison.Ordinal);
    }

    /// <summary>More images than a release may pin, refused before any registry is asked.</summary>
    [Fact]
    public async Task Publish_RefusesMoreImagesThanTheLimit() {
        var resolver = new StubDigestResolver { Digest = ApiDigest };
        using var host = StartHost(resolver);
        var productId = await SeedProductAsync(host);
        var images = Enumerable.Range(0, ReleaseIntakeService.MaxImages + 1)
            .Select(i => $"docker.io/acme/api{i}@{ApiDigest}")
            .ToList();

        var result = await PublishAsync(host, Request(productId, images));

        Assert.Equal(ReleaseIntakeStatus.Invalid, result.Status);
        Assert.Empty(resolver.Asked);
    }

    // ── the mode flip and the roll-out hook ──────────────────────────────────

    /// <summary>
    /// The moment a product's stacks stop deploying branch heads (ADR-0026 decision 5): the first
    /// accepted release flips the mode, in the same write, and says so in the trail.
    /// </summary>
    [Fact]
    public async Task Publish_FlipsAGitModeProductIntoReleaseModeAndAuditsIt() {
        using var host = StartHost(new StubDigestResolver { Digest = ApiDigest });
        var productId = await SeedProductAsync(host);
        Assert.Equal(ProductReleaseMode.Git, await ModeAsync(host, productId));

        var result = await PublishAsync(host, Request(productId, [$"docker.io/acme/api@{ApiDigest}"]));

        Assert.Equal(ReleaseIntakeStatus.Created, result.Status);
        Assert.Equal(ProductReleaseMode.Releases, await ModeAsync(host, productId));
        var audit = Assert.Single(await AuditAsync(host, ReleaseIntakeService.ModeChangeAction));
        Assert.Equal("shop", audit.Target);
        Assert.Contains("first release", audit.Detail!, StringComparison.Ordinal);
        Assert.Contains("Git → Releases", audit.Detail!, StringComparison.Ordinal);
        // Actor-less: the webhook has nobody signed in behind it.
        Assert.Null(audit.Actor);
    }

    /// <summary>
    /// The flip happens once. A second release of a product already in release mode changes nothing and
    /// records no second mode change — and neither does a replay of the first.
    /// </summary>
    [Fact]
    public async Task Publish_DoesNotRecordASecondModeChange() {
        using var host = StartHost(new StubDigestResolver { Digest = ApiDigest });
        var productId = await SeedProductAsync(host);
        var first = Request(productId, [$"docker.io/acme/api@{ApiDigest}"], version: "1.0.0");

        await PublishAsync(host, first);
        await PublishAsync(host, first);   // the retried curl
        await PublishAsync(host, Request(productId, [$"docker.io/acme/api@{WorkerDigest}"], version: "1.0.1"));

        Assert.Single(await AuditAsync(host, ReleaseIntakeService.ModeChangeAction));
        Assert.Equal(ProductReleaseMode.Releases, await ModeAsync(host, productId));
    }

    /// <summary>
    /// A refused release leaves the mode alone — the flip rides in the same write as the insert, so
    /// there is no state in which a product deploys releases it does not have.
    /// </summary>
    [Fact]
    public async Task Publish_LeavesTheModeAloneWhenTheReleaseIsRefused() {
        using var host = StartHost(new StubDigestResolver { Digest = ApiDigest });
        var productId = await SeedProductAsync(host);

        var refused = await PublishAsync(host, Request(productId, ["registry.invalid/acme/api:1"]));

        Assert.Equal(ReleaseIntakeStatus.Invalid, refused.Status);
        Assert.Equal(ProductReleaseMode.Git, await ModeAsync(host, productId));
        Assert.Empty(await AuditAsync(host, ReleaseIntakeService.ModeChangeAction));
    }

    /// <summary>
    /// The roll-out hook runs for a created release and never for a replay — the property that makes
    /// <c>curl --retry</c> safe to put in a workflow — and its count reaches the audit row.
    /// </summary>
    [Fact]
    public async Task Publish_RunsTheRolloutHookOnceAndOnlyForACreatedRelease() {
        using var host = StartHost(new StubDigestResolver { Digest = ApiDigest });
        var productId = await SeedProductAsync(host);
        var request = Request(productId, [$"docker.io/acme/api@{ApiDigest}"], version: "1.0.0");
        var calls = 0;

        var created = await PublishWithHookAsync(host, request, () => { calls++; return 3; });
        var replay = await PublishWithHookAsync(host, request, () => { calls++; return 3; });

        Assert.Equal(ReleaseIntakeStatus.Created, created.Status);
        Assert.Equal(3, created.StacksEnqueued);
        Assert.Equal(ReleaseIntakeStatus.Replayed, replay.Status);
        Assert.Equal(0, replay.StacksEnqueued);
        Assert.Equal(1, calls);

        var publish = Assert.Single(await AuditAsync(host, ReleaseIntakeService.PublishAction));
        Assert.Contains("3 stack(s) enqueued", publish.Detail!, StringComparison.Ordinal);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static AuthTestHost StartHost(IReleaseDigestResolver resolver) =>
        // Registered after AddWatchtowerServices, so this wins over the shipped registration.
        AuthTestHost.Start(services => services.AddSingleton(resolver));

    private static ReleaseIntakeRequest Request(
        int productId, IReadOnlyList<string> images, string? version = null, string branch = "main") =>
        new(productId, images, Release.ViaWebhook,
            CommitSha: Commit, Branch: branch, Version: version, CallerIp: "203.0.113.7");

    private static async Task<ReleaseIntakeResult> PublishAsync(AuthTestHost host, ReleaseIntakeRequest request) {
        // A scope per call, like a request: the second report of a retried curl is a second request.
        await using var scope = host.Services.CreateAsyncScope();
        var intake = scope.ServiceProvider.GetRequiredService<ReleaseIntakeService>();
        return await intake.PublishAsync(request, Ct);
    }

    /// <summary>Publishes with a roll-out hook, so the "created only" rule is observable.</summary>
    private static async Task<ReleaseIntakeResult> PublishWithHookAsync(
        AuthTestHost host, ReleaseIntakeRequest request, Func<int> onCreated) {
        await using var scope = host.Services.CreateAsyncScope();
        var intake = scope.ServiceProvider.GetRequiredService<ReleaseIntakeService>();
        return await intake.PublishAsync(request, (_, _) => Task.FromResult(onCreated()), Ct);
    }

    private static async Task<ProductReleaseMode> ModeAsync(AuthTestHost host, int productId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Products.AsNoTracking()
            .Where(p => p.Id == productId).Select(p => p.ReleaseMode).FirstAsync(Ct);
    }

    private static async Task<int> SeedProductAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var product = TestProducts.New("shop", "https://github.com/acme/shop.git");
        db.Products.Add(product);
        await db.SaveChangesAsync(Ct);
        return product.Id;
    }

    private static async Task AddRegistryAsync(AuthTestHost host, string url) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var credential = new Credential {
            Name = "registry", Username = "registry-user", Token = "registry-secret",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Credentials.Add(credential);
        db.Registries.Add(new Registry { Name = url, Url = url, Credential = credential });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<List<Release>> ReleasesAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.Releases.AsNoTracking().OrderBy(r => r.Id).ToListAsync(Ct);
    }

    private static async Task<List<(string Repository, string? Tag, string Digest)>> ImagesAsync(
        AuthTestHost host, int releaseId) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        // Projected as an anonymous type and tupled client-side: the Npgsql data source maps records as
        // composite tuples, so a ValueTuple projection would be read back as a PostgreSQL record.
        var rows = await db.ReleaseImages.AsNoTracking()
            .Where(i => i.ReleaseId == releaseId)
            .OrderBy(i => i.Repository)
            .Select(i => new { i.Repository, i.Tag, i.Digest })
            .ToListAsync(Ct);
        return [.. rows.Select(r => (r.Repository, r.Tag, r.Digest))];
    }

    private static async Task<List<AuditEvent>> AuditAsync(AuthTestHost host, string action) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .Where(e => e.Category == "products" && e.Action == action)
            .OrderBy(e => e.Id)
            .ToListAsync(Ct);
    }

    /// <summary>A registry that answers whatever the test says, and remembers what it was asked.</summary>
    private sealed class StubDigestResolver : IReleaseDigestResolver {
        private readonly Lock _gate = new();
        private readonly List<(string Image, string? Username, string? Password)> _asked = [];

        /// <summary>The digest every lookup resolves to, unless <see cref="Answer"/> says otherwise.</summary>
        public string? Digest { get; init; }

        /// <summary>A fixed outcome for every lookup — for the not-found and unreachable cases.</summary>
        public ReleaseDigestResult? Answer { get; init; }

        /// <summary>What was actually looked up, in call order.</summary>
        public IReadOnlyList<(string Image, string? Username, string? Password)> Asked {
            get { lock (_gate) return [.. _asked]; }
        }

        public Task<ReleaseDigestResult> ResolveAsync(
            string imageReference, string? username, string? password, CancellationToken ct) {
            lock (_gate) _asked.Add((imageReference, username, password));
            return Task.FromResult(
                Answer ?? ReleaseDigestResult.Resolved(Digest ?? throw new InvalidOperationException(
                    "The stub was asked to resolve a tag but was given neither a digest nor an answer.")));
        }
    }

    /// <summary>
    /// Intake whose idempotency pre-check misses exactly once — the losing side of two identical
    /// concurrent reports, without having to win a race to observe it.
    /// </summary>
    private sealed class BlindIntakeService(
        WatchtowerDbContext db, RegistryAuthBuilder registries, IReleaseDigestResolver digests,
        AuditLog audit, ReleasePruner pruner,
        Microsoft.Extensions.Logging.ILogger<ReleaseIntakeService> logger, TimeProvider time)
        : ReleaseIntakeService(db, registries, digests, audit, pruner, logger, time) {
        private bool _blinded;

        protected override Task<(Release? Replay, bool VersionTaken)> PrecheckAsync(
            int productId, string fingerprint, string version, CancellationToken ct) {
            if (_blinded) return base.PrecheckAsync(productId, fingerprint, version, ct);
            _blinded = true;
            // What the loser of the race sees: the winner has not committed yet, so neither the
            // fingerprint nor the version is there to be found.
            return Task.FromResult<(Release?, bool)>((null, false));
        }
    }
}
