using Elarion.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Products.Handlers;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The <c>products.*</c> release surface (ADR-0026 stage 3): listing with keyset paging, the expanded
/// release, the manual create path, deletion, and the two webhook-token handlers.
/// </summary>
/// <remarks>
/// Every image here is a digest reference, so no registry is ever asked — the resolution rules have
/// their own suite (<see cref="ReleaseIntakeTests"/>) and these tests are about the handlers.
/// </remarks>
public sealed class ReleasesRpcTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Commit = "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678";
    private const string ApiDigest = "sha256:1111111111111111111111111111111111111111111111111111111111111111";
    private const string WorkerDigest = "sha256:2222222222222222222222222222222222222222222222222222222222222222";

    // ── listing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Newest first, by id — never by timestamp (ADR-0026) — and "show older" pages by the last id it
    /// saw rather than by an offset, so a release published mid-paging cannot shift the window.
    /// </summary>
    [Fact]
    public async Task ListReleases_PagesNewestFirstOnTheId() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var productId = await SeedProductAsync(scope.ServiceProvider);
        var ids = new List<int>();
        for (var i = 1; i <= 5; i++) ids.Add(await SeedReleaseAsync(scope.ServiceProvider, productId, $"1.{i}.0"));

        var first = await ListAsync(scope.ServiceProvider, productId, limit: 2);
        Assert.True(first.IsSuccess, Describe(first));
        Assert.Equal([ids[4], ids[3]], first.Value.Releases.Select(r => r.Id));
        Assert.True(first.Value.HasMore);
        Assert.Equal("1.5.0", first.Value.Releases[0].Version);
        // The row carries a count, not the digests — those are behind the expansion.
        Assert.Equal(1, first.Value.Releases[0].ImageCount);

        var next = await ListAsync(scope.ServiceProvider, productId, limit: 2, before: ids[3]);
        Assert.Equal([ids[2], ids[1]], next.Value.Releases.Select(r => r.Id));
        Assert.True(next.Value.HasMore);

        var last = await ListAsync(scope.ServiceProvider, productId, limit: 2, before: ids[1]);
        Assert.Equal([ids[0]], last.Value.Releases.Select(r => r.Id));
        Assert.False(last.Value.HasMore);
    }

    [Fact]
    public async Task ListReleases_RefusesAProductThatDoesNotExist() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();

        var missing = await ListAsync(scope.ServiceProvider, productId: 4242);

        Assert.False(missing.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, missing.Error.Kind);
    }

    /// <summary>The expansion: the digest table and the notes the list deliberately leaves out.</summary>
    [Fact]
    public async Task GetRelease_CarriesTheImagesAndTheNotes() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var productId = await SeedProductAsync(scope.ServiceProvider);

        var created = await CreateAsync(
            scope.ServiceProvider, productId, "1.4.0",
            [$"docker.io/acme/worker@{WorkerDigest}", $"docker.io/acme/api@{ApiDigest}"],
            notes: "First cut.");
        Assert.True(created.IsSuccess, Describe(created));

        var detail = await ActivatorUtilities.CreateInstance<GetRelease>(scope.ServiceProvider)
            .HandleAsync(new GetRelease.Query(created.Value.Release.Id), Ct);

        Assert.True(detail.IsSuccess, Describe(detail));
        Assert.Equal("shop", detail.Value.Release.ProductName);
        Assert.Equal("First cut.", detail.Value.Release.Notes);
        // Ordered by repository, so the table does not reshuffle between reads.
        Assert.Equal(
            ["docker.io/acme/api", "docker.io/acme/worker"],
            detail.Value.Release.Images.Select(i => i.Repository));
        Assert.Equal(ApiDigest, detail.Value.Release.Images[0].Digest);
    }

    // ── manual create ────────────────────────────────────────────────────────

    /// <summary>
    /// The manual path runs the same pipeline as the webhook, and differs in exactly two places: it is
    /// recorded as <c>manual</c>, and the audit row names who did it.
    /// </summary>
    [Fact]
    public async Task CreateRelease_RecordsAManualReleaseAndAuditsTheActor() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var productId = await SeedProductAsync(scope.ServiceProvider);

        var created = await CreateAsync(
            scope.ServiceProvider, productId, "1.4.0", [$"docker.io/acme/api@{ApiDigest}"]);

        Assert.True(created.IsSuccess, Describe(created));
        Assert.Equal(Release.ViaManual, created.Value.Release.CreatedVia);
        // No branch was asked for, so it is the product's own.
        Assert.Equal("main", created.Value.Release.Branch);
        Assert.Equal(Commit, created.Value.Release.CommitSha);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var audit = await db.AuditEvents.AsNoTracking().SingleAsync(
            e => e.Category == "products" && e.Action == ReleaseIntakeService.PublishAction, Ct);
        Assert.Equal("shop/1.4.0", audit.Target);
        Assert.Equal(ImplicitAdminCurrentUser.LocalUserId, audit.Actor);
        Assert.Contains("source manual", audit.Detail!, StringComparison.Ordinal);
        // Nobody called over HTTP, so there is no address to record.
        Assert.DoesNotContain(" from ", audit.Detail!, StringComparison.Ordinal);
    }

    /// <summary>A reused version for a different build is a conflict, and the error says which one.</summary>
    [Fact]
    public async Task CreateRelease_RefusesAVersionThatIsAlreadyTaken() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var productId = await SeedProductAsync(scope.ServiceProvider);
        await CreateAsync(scope.ServiceProvider, productId, "1.4.0", [$"docker.io/acme/api@{ApiDigest}"]);

        var clash = await CreateAsync(
            scope.ServiceProvider, productId, "1.4.0", [$"docker.io/acme/api@{WorkerDigest}"]);

        Assert.False(clash.IsSuccess);
        Assert.Equal(ErrorKind.Conflict, clash.Error.Kind);
        Assert.Contains("1.4.0", clash.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>Recording the identical build twice is not an error — it answers with what exists.</summary>
    [Fact]
    public async Task CreateRelease_AnswersARepeatWithTheReleaseThatAlreadyExists() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var productId = await SeedProductAsync(scope.ServiceProvider);

        var first = await CreateAsync(scope.ServiceProvider, productId, "1.4.0", [$"docker.io/acme/api@{ApiDigest}"]);
        var again = await CreateAsync(scope.ServiceProvider, productId, "1.4.0", [$"docker.io/acme/api@{ApiDigest}"]);

        Assert.True(again.IsSuccess, Describe(again));
        Assert.Equal(first.Value.Release.Id, again.Value.Release.Id);

        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Single(await db.Releases.AsNoTracking().ToListAsync(Ct));
    }

    [Fact]
    public async Task CreateRelease_RefusesAProductThatDoesNotExist() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();

        var missing = await CreateAsync(scope.ServiceProvider, 4242, "1.0.0", [$"docker.io/acme/api@{ApiDigest}"]);

        Assert.False(missing.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, missing.Error.Kind);
    }

    // ── delete ───────────────────────────────────────────────────────────────

    /// <summary>Deleting takes the images with it and leaves a trail naming the release.</summary>
    [Fact]
    public async Task DeleteRelease_RemovesTheImagesAndAudits() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var productId = await SeedProductAsync(scope.ServiceProvider);
        var created = await CreateAsync(
            scope.ServiceProvider, productId, "1.4.0", [$"docker.io/acme/api@{ApiDigest}"]);

        var deleted = await ActivatorUtilities.CreateInstance<DeleteRelease>(scope.ServiceProvider)
            .HandleAsync(new DeleteRelease.Command(created.Value.Release.Id), Ct);

        Assert.True(deleted.IsSuccess, Describe(deleted));
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        Assert.Empty(await db.Releases.AsNoTracking().ToListAsync(Ct));
        Assert.Empty(await db.ReleaseImages.AsNoTracking().ToListAsync(Ct));

        var audit = await db.AuditEvents.AsNoTracking()
            .SingleAsync(e => e.Action == DeleteRelease.AuditAction, Ct);
        Assert.Equal("shop/1.4.0", audit.Target);
        Assert.Equal(ImplicitAdminCurrentUser.LocalUserId, audit.Actor);
    }

    [Fact]
    public async Task DeleteRelease_RefusesOneThatDoesNotExist() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();

        var missing = await ActivatorUtilities.CreateInstance<DeleteRelease>(scope.ServiceProvider)
            .HandleAsync(new DeleteRelease.Command(4242), Ct);

        Assert.False(missing.IsSuccess);
        Assert.Equal(ErrorKind.NotFound, missing.Error.Kind);
    }

    // ── the webhook token ────────────────────────────────────────────────────

    /// <summary>
    /// Rotating hands back a usable token, which means the webhook is on: a token the endpoint would
    /// answer 404 for is a trap, so enabling is part of rotating.
    /// </summary>
    [Fact]
    public async Task RotateReleaseToken_GeneratesATokenAndEnablesTheWebhook() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var productId = await SeedProductAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        var first = await RotateAsync(scope.ServiceProvider, productId);

        Assert.True(first.IsSuccess, Describe(first));
        Assert.StartsWith(ReleaseWebhookTokens.Prefix, first.Value.Token, StringComparison.Ordinal);
        Assert.True(first.Value.Enabled);
        var stored = await db.Products.AsNoTracking().SingleAsync(p => p.Id == productId, Ct);
        Assert.Equal(first.Value.Token, stored.ReleaseWebhookToken);
        Assert.True(stored.ReleaseWebhookEnabled);

        // Rotating again replaces it — the previous value stops working immediately.
        var second = await RotateAsync(scope.ServiceProvider, productId);
        Assert.NotEqual(first.Value.Token, second.Value.Token);

        var audits = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == RotateReleaseToken.AuditAction).OrderBy(e => e.Id).ToListAsync(Ct);
        Assert.Equal(2, audits.Count);
        Assert.Equal("shop", audits[0].Target);
        Assert.Contains("generated", audits[0].Detail!, StringComparison.Ordinal);
        Assert.Contains("replaced", audits[1].Detail!, StringComparison.Ordinal);
        Assert.Equal(ImplicitAdminCurrentUser.LocalUserId, audits[0].Actor);
    }

    /// <summary>
    /// Enabling with no token generates one, so "enabled" and "has a token" never come apart in the
    /// direction the endpoint treats as closed; disabling keeps the token, so re-enabling does not
    /// invalidate a secret somebody already pasted into their CI.
    /// </summary>
    [Fact]
    public async Task SetReleaseWebhook_GeneratesOnEnableAndKeepsTheTokenOnDisable() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var productId = await SeedProductAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        var enabled = await ToggleAsync(scope.ServiceProvider, productId, true);
        Assert.True(enabled.IsSuccess, Describe(enabled));
        Assert.True(enabled.Value.Enabled);
        // The response deliberately carries no token; products.get is where it is served.
        var generated = (await db.Products.AsNoTracking().SingleAsync(p => p.Id == productId, Ct))
            .ReleaseWebhookToken;
        Assert.StartsWith(ReleaseWebhookTokens.Prefix, generated!, StringComparison.Ordinal);

        var disabled = await ToggleAsync(scope.ServiceProvider, productId, false);
        Assert.False(disabled.Value.Enabled);

        var stored = await db.Products.AsNoTracking().SingleAsync(p => p.Id == productId, Ct);
        Assert.False(stored.ReleaseWebhookEnabled);
        // Kept, so re-enabling does not invalidate a secret somebody already pasted into their CI.
        Assert.Equal(generated, stored.ReleaseWebhookToken);

        var audits = await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == SetReleaseWebhook.AuditAction).OrderBy(e => e.Id).ToListAsync(Ct);
        Assert.Equal(2, audits.Count);
        Assert.Contains("token generated", audits[0].Detail!, StringComparison.Ordinal);
        Assert.Equal("disabled", audits[1].Detail);
    }

    /// <summary>A toggle that changes nothing writes nothing, so the trail stays readable.</summary>
    [Fact]
    public async Task SetReleaseWebhook_IsSilentWhenNothingChanges() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var productId = await SeedProductAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();

        var unchanged = await ToggleAsync(scope.ServiceProvider, productId, false);

        Assert.True(unchanged.IsSuccess, Describe(unchanged));
        Assert.False(unchanged.Value.Enabled);
        Assert.Null((await db.Products.AsNoTracking().SingleAsync(p => p.Id == productId, Ct))
            .ReleaseWebhookToken);
        Assert.Empty(await db.AuditEvents.AsNoTracking()
            .Where(e => e.Action == SetReleaseWebhook.AuditAction).ToListAsync(Ct));
    }

    // ── what the product surfaces ────────────────────────────────────────────

    /// <summary>
    /// What the Releases tab reads off the product: the enabled flag and the latest release on the DTO,
    /// and the token only on the detail response — the catalogue must not carry every product's secret.
    /// </summary>
    [Fact]
    public async Task GetProduct_CarriesTheWebhookStateAndTheLatestRelease() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var productId = await SeedProductAsync(scope.ServiceProvider);
        await CreateAsync(scope.ServiceProvider, productId, "1.4.0", [$"docker.io/acme/api@{ApiDigest}"]);
        var newest = await CreateAsync(
            scope.ServiceProvider, productId, "1.5.0", [$"docker.io/acme/api@{WorkerDigest}"]);
        var rotated = await RotateAsync(scope.ServiceProvider, productId);

        var detail = await ActivatorUtilities.CreateInstance<GetProduct>(scope.ServiceProvider)
            .HandleAsync(new GetProduct.Query(productId), Ct);

        Assert.True(detail.IsSuccess, Describe(detail));
        Assert.True(detail.Value.Product.ReleaseWebhookEnabled);
        Assert.Equal(rotated.Value.Token, detail.Value.ReleaseWebhookToken);
        Assert.Equal(newest.Value.Release.Id, detail.Value.Product.LatestRelease!.Id);
        Assert.Equal("1.5.0", detail.Value.Product.LatestRelease.Version);

        // …and the catalogue answers the same latest release without the token.
        var list = await ActivatorUtilities.CreateInstance<ListProducts>(scope.ServiceProvider)
            .HandleAsync(new ListProducts.Query(), Ct);
        var row = Assert.Single(list.Value.Products);
        Assert.Equal("1.5.0", row.LatestRelease!.Version);
        Assert.True(row.ReleaseWebhookEnabled);
    }

    /// <summary>A product with no releases says so, rather than inventing a placeholder.</summary>
    [Fact]
    public async Task GetProduct_HasNoLatestReleaseBeforeThereIsOne() {
        using var host = AuthTestHost.Start();
        await using var scope = host.Services.CreateAsyncScope();
        var productId = await SeedProductAsync(scope.ServiceProvider);

        var detail = await ActivatorUtilities.CreateInstance<GetProduct>(scope.ServiceProvider)
            .HandleAsync(new GetProduct.Query(productId), Ct);

        Assert.Null(detail.Value.Product.LatestRelease);
        Assert.False(detail.Value.Product.ReleaseWebhookEnabled);
        Assert.Null(detail.Value.ReleaseWebhookToken);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static Task<Result<ListReleases.Response>> ListAsync(
        IServiceProvider services, int productId, int limit = 20, int? before = null) =>
        ActivatorUtilities.CreateInstance<ListReleases>(services)
            .HandleAsync(new ListReleases.Query(productId, before, limit), Ct).AsTask();

    private static Task<Result<CreateRelease.Response>> CreateAsync(
        IServiceProvider services, int productId, string version, IReadOnlyList<string> images,
        string? notes = null) =>
        ActivatorUtilities.CreateInstance<CreateRelease>(services)
            .HandleAsync(new CreateRelease.Command(productId, version, images, Commit, notes), Ct).AsTask();

    private static Task<Result<RotateReleaseToken.Response>> RotateAsync(
        IServiceProvider services, int productId) =>
        ActivatorUtilities.CreateInstance<RotateReleaseToken>(services)
            .HandleAsync(new RotateReleaseToken.Command(productId), Ct).AsTask();

    private static Task<Result<SetReleaseWebhook.Response>> ToggleAsync(
        IServiceProvider services, int productId, bool enabled) =>
        ActivatorUtilities.CreateInstance<SetReleaseWebhook>(services)
            .HandleAsync(new SetReleaseWebhook.Command(productId, enabled), Ct).AsTask();

    private static async Task<int> SeedProductAsync(IServiceProvider services) {
        var db = services.GetRequiredService<WatchtowerDbContext>();
        var product = TestProducts.New("shop", "https://github.com/acme/shop.git");
        db.Products.Add(product);
        await db.SaveChangesAsync(Ct);
        return product.Id;
    }

    /// <summary>A release written straight to the database — the listing does not care how it arrived.</summary>
    private static async Task<int> SeedReleaseAsync(IServiceProvider services, int productId, string version) {
        var db = services.GetRequiredService<WatchtowerDbContext>();
        var release = new Release {
            ProductId = productId,
            Version = version,
            CommitSha = Commit,
            Branch = "main",
            Fingerprint = $"fingerprint-{version}",
            CreatedVia = Release.ViaWebhook,
            CreatedAt = DateTimeOffset.UtcNow,
            Images = [new ReleaseImage { Repository = "docker.io/acme/api", Tag = version, Digest = ApiDigest }],
        };
        db.Releases.Add(release);
        await db.SaveChangesAsync(Ct);
        return release.Id;
    }

    private static string Describe<T>(Result<T> result) =>
        result.IsSuccess ? "success" : $"{result.Error.Kind}: {result.Error.Message}";
}
