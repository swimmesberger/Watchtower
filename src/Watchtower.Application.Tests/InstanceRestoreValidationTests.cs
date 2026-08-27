using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// What an instance refuses to restore, and why (ADR-0027 §5). Every one of these decisions is made
/// <em>before</em> anything is touched, which is the point: an instance that cannot read the bundle it
/// was handed has to still be the instance it was. The messages are asserted as well as the outcomes —
/// a refusal an operator cannot act on is only half a refusal.
/// </summary>
public sealed class InstanceRestoreValidationTests {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthTestHost Start(params (string, string?)[] settings) =>
        AuthTestHost.Start(FakeSelfPostgresLocator.Register, settings);

    /// <summary>The migration this build actually applied, so a default bundle is a valid one.</summary>
    private static async Task<string?> LastMigrationAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await InstanceVersion.LastMigrationAsync(db, Ct);
    }

    private static async Task<RestoreValidation> StageAsync(AuthTestHost host, byte[] bundle) {
        using var stream = new MemoryStream(bundle);
        return await host.Services.GetRequiredService<InstanceRestoreService>().StageAsync(stream, Ct);
    }

    private static async Task<RestoreValidation> StageDefaultAsync(
        AuthTestHost host, TestBundles.Options? options = null) =>
        await StageAsync(host, TestBundles.Build(await LastMigrationAsync(host), options));

    private static string? Blocking(RestoreValidation validation, string code) =>
        validation.Blocking.FirstOrDefault(f => f.Code == code)?.Message;

    [Fact]
    public async Task AValidBundleIsAccepted() {
        using var host = Start();

        var validation = await StageDefaultAsync(host, new TestBundles.Options(
            Stacks: ["blog", "shop"], MissingStacks: ["never-backed-up"]));

        Assert.True(validation.CanRestore, string.Join(" ", validation.Blocking.Select(b => b.Message)));
        Assert.Empty(validation.Blocking);
        Assert.Equal("source", validation.InstanceName);
        Assert.Equal("9.9.9-test", validation.AppVersion);
        Assert.Equal(2, validation.StackCount);
        Assert.Equal(1, validation.MissingStackCount);
        Assert.Equal(["blog", "shop", "never-backed-up"], validation.StackNames);
    }

    [Fact]
    public async Task ABundleFromANewerSchemaIsRefused() {
        // Migrations only roll forward, so this is exact rather than a version comparison: replaying a
        // schema this binary has never known would leave a database it cannot migrate.
        using var host = Start();

        var validation = await StageDefaultAsync(
            host, new TestBundles.Options(LastMigrationId: "29990101000000_FromTheFuture"));

        Assert.False(validation.CanRestore);
        var message = Blocking(validation, "newer-schema");
        Assert.NotNull(message);
        Assert.Contains("9.9.9-test", message);
        Assert.Contains("Update this Watchtower", message);
    }

    [Fact]
    public async Task AMismatchedKeyProtectionSecretIsRefusedWithTheRemedy() {
        // The sharpest edge in the feature: the stored certificates and keys are AES-GCM under this
        // secret, and it cannot be changed at runtime — so the message has to name the variable and say
        // that a restart is part of the fix.
        using var host = Start(("Watchtower:Auth:KeyProtectionSecret", "this-instances-secret"));

        var validation = await StageDefaultAsync(
            host, new TestBundles.Options(KeyProtectionSecret: "the-source-instances-secret"));

        Assert.False(validation.CanRestore);
        var message = Blocking(validation, "key-protection-secret");
        Assert.NotNull(message);
        Assert.Contains("WATCHTOWER__AUTH__KEYPROTECTIONSECRET", message);
        Assert.Contains("restart", message);
        // Never the secret itself, from either side.
        Assert.DoesNotContain("this-instances-secret", message);
        Assert.DoesNotContain("the-source-instances-secret", message);
    }

    [Fact]
    public async Task AMatchingKeyProtectionSecretIsAccepted() {
        using var host = Start(("Watchtower:Auth:KeyProtectionSecret", "shared-secret"));

        var validation = await StageDefaultAsync(host, new TestBundles.Options(KeyProtectionSecret: "shared-secret"));

        Assert.True(validation.CanRestore);
    }

    [Fact]
    public async Task ABundleWithNoSecretIntoAnInstanceThatHasOneIsAWarningNotARefusal() {
        // The restored rows are readable as they are, and later writes are encrypted. Worth saying,
        // not worth stopping for.
        using var host = Start(("Watchtower:Auth:KeyProtectionSecret", "this-instances-secret"));

        var validation = await StageDefaultAsync(host);

        Assert.True(validation.CanRestore);
        Assert.Contains(validation.Warnings, w => w.Code == "key-protection-secret-new");
    }

    [Fact]
    public async Task AnArchiveThatDoesNotMatchItsChecksumIsRefused() {
        using var host = Start();

        var validation = await StageDefaultAsync(host, new TestBundles.Options(CorruptInstanceDigest: true));

        Assert.False(validation.CanRestore);
        Assert.Contains("damaged in transit or altered", Blocking(validation, "corrupt-archive"));
    }

    [Fact]
    public async Task AnArchiveTheManifestPromisesButTheTarLacksIsRefused() {
        using var host = Start();

        var validation = await StageDefaultAsync(host, new TestBundles.Options(OmitInstanceArchive: true));

        Assert.False(validation.CanRestore);
        Assert.Contains("incomplete or was repacked", Blocking(validation, "missing-archive"));
    }

    [Fact]
    public async Task AnArchiveTheBundlesOwnPassphraseCannotOpenIsRefused() {
        // Proving the passphrase now is the whole reason the probe exists: discovering it after the
        // database has been dropped would be discovering it too late.
        using var host = Start();

        var validation = await StageDefaultAsync(host, new TestBundles.Options(WrongPassphrase: true));

        Assert.False(validation.CanRestore);
        Assert.NotNull(Blocking(validation, "unreadable-archive"));
    }

    [Fact]
    public async Task AnArchiveWithNoDumpInItIsRefused() {
        using var host = Start();

        var validation = await StageDefaultAsync(host, new TestBundles.Options(WithoutDump: true));

        Assert.False(validation.CanRestore);
        Assert.Contains("nothing to restore from", Blocking(validation, "no-dump"));
    }

    [Fact]
    public async Task AnUnknownBundleFormatIsRefused() {
        using var host = Start();

        var validation = await StageDefaultAsync(host, new TestBundles.Options(BundleFormatVersion: 99));

        Assert.False(validation.CanRestore);
        Assert.Contains("format version 99", Blocking(validation, "bundle-format"));
    }

    [Fact]
    public async Task RestoringOverAWatchtowerThatIsInUseWarnsAboutWhatItReplaces() {
        using var host = Start();
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            db.Stacks.Add(new Stack {
                Name = "already-here", ComposeProjectName = "already-here",
                Product = TestProducts.New("already-here"),
            });
            await db.SaveChangesAsync(Ct);
        }

        var validation = await StageDefaultAsync(host);

        // A warning, not a refusal: replacing a working instance is a thing an operator may legitimately
        // mean to do. The confirmation dialog is where they say so.
        Assert.True(validation.CanRestore);
        Assert.Contains("keep running unmanaged", validation.Warnings.Single(w => w.Code == "not-fresh").Message);
    }

    [Fact]
    public async Task AFreshInstanceDoesNotGetTheWarning() {
        using var host = Start();

        var validation = await StageDefaultAsync(host);

        Assert.DoesNotContain(validation.Warnings, w => w.Code == "not-fresh");
    }

    [Theory]
    [InlineData("../escaped.json")]
    [InlineData("stacks/../../escaped.json")]
    [InlineData("/etc/watchtower-escaped.json")]
    public async Task ATarThatWouldWriteOutsideItsDirectoryIsRejectedOutright(string entryName) {
        // The entry names come from a file an operator uploaded, so this is the one check that has to
        // happen before anything is written rather than after everything is. Refused rather than
        // sanitized: a name that tries to escape is a name to stop on, not one to quietly rewrite.
        using var host = Start();
        using var stream = new MemoryStream(TestBundles.TraversalBundle(entryName));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Services.GetRequiredService<InstanceRestoreService>().StageAsync(stream, Ct));

        Assert.Contains("written outside it", error.Message);
    }

    [Fact]
    public async Task AnUploadThatIsNotABundleSaysSo() {
        using var host = Start();
        using var stream = new MemoryStream(TestBundles.NotABundle());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.Services.GetRequiredService<InstanceRestoreService>().StageAsync(stream, Ct));

        Assert.Contains("not a Watchtower backup bundle", error.Message);
    }

    [Fact]
    public async Task AStagedBundleCanBeRevalidatedWithoutReUploadingIt() {
        // What the wizard does on a page load: the bundle is already here, and re-hashing gigabytes to
        // answer that would be the wrong trade.
        using var host = Start();
        await StageDefaultAsync(host, new TestBundles.Options(Stacks: ["blog"]));

        var staging = host.Services.GetRequiredService<InstanceRestoreStaging>();
        var staged = Assert.IsType<StagedRestore>(staging.Current);
        var revalidated = await host.Services.GetRequiredService<InstanceRestoreService>()
            .ValidateAsync(staged, Ct);

        Assert.True(revalidated.CanRestore);
        Assert.Equal(1, revalidated.StackCount);
    }
}
