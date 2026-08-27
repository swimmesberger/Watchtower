using Elarion.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// How the first start after a restore works out whether the restore happened (ADR-0027 §5), and what
/// it repairs when it did.
/// </summary>
/// <remarks>
/// The verdict rests entirely on the nonce: the restore writes one into the database it is about to
/// replace, and only a replay can remove it. That is the whole reason this can be decided at all — the
/// process that would have watched the coordinator is the process the coordinator stopped.
/// </remarks>
public sealed class RestoreCompletionTests : IDisposable {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _stagingRoot =
        Directory.CreateTempSubdirectory("wt-restore-completion").FullName;

    public void Dispose() {
        try {
            Directory.Delete(_stagingRoot, recursive: true);
        } catch (IOException) {
            // Scratch space the OS reclaims; never worth failing a passing test over.
        }
    }

    /// <summary>A host whose restore staging is this test's own directory, not the shared temp one.</summary>
    private AuthTestHost Start() =>
        AuthTestHost.Start(
            services => services.Replace(ServiceDescriptor.Singleton(sp =>
                new InstanceRestoreStaging(
                    sp.GetService<ILogger<InstanceRestoreStaging>>()
                    ?? NullLogger<InstanceRestoreStaging>.Instance,
                    _stagingRoot))));

    private static InstanceRestoreStaging Staging(AuthTestHost host) =>
        host.Services.GetRequiredService<InstanceRestoreStaging>();

    /// <summary>Writes the marker a restore leaves behind, as the real one does.</summary>
    private static Task MarkInFlightAsync(AuthTestHost host, string nonce, params string[] stacks) =>
        Staging(host).WriteProgressAsync(
            new RestoreProgress(nonce, DateTimeOffset.UtcNow, "source", "coordinator-id", stacks), Ct);

    /// <summary>Writes the nonce row into the database, as the real restore does before handing over.</summary>
    private static async Task WriteNonceAsync(AuthTestHost host, string nonce) {
        await using var scope = host.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        await settings.SetStringAsync(
            WatchtowerSettingPaths.RestorePendingNonce, nonce, SettingsScope.Global,
            expectedVersion: null, Ct);
    }

    private static async Task<string?> ReadSettingAsync(AuthTestHost host, string path) {
        await using var scope = host.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        return await settings.GetStringAsync(path, SettingsScope.Global, Ct);
    }

    /// <summary>Runs the completion pass the way the host start does.</summary>
    private static async Task<RestoreCompletionService> CompleteAsync(AuthTestHost host) {
        var completion = host.Services.GetRequiredService<RestoreCompletionService>();
        await completion.StartAsync(Ct);
        return completion;
    }

    private static async Task<int> AddStackAsync(AuthTestHost host, string name, DateTimeOffset? cursor) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = name,
            ComposeProjectName = name,
            Product = TestProducts.New(name),
            LastScheduledBackupAt = cursor,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);
        return stack.Id;
    }

    private static async Task<AuditEvent?> RestoreAuditAsync(AuthTestHost host) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.AuditEvents.AsNoTracking()
            .FirstOrDefaultAsync(a => a.Action == "instance.restore", Ct);
    }

    [Fact]
    public async Task WithNoMarkerNothingHappens() {
        // The ordinary start, which every instance that has never restored anything does forever.
        using var host = Start();

        var completion = await CompleteAsync(host);

        Assert.Equal(RestoreOutcome.None, completion.LastOutcome);
        Assert.Null(await RestoreAuditAsync(host));
    }

    [Fact]
    public async Task TheNonceBeingGoneMeansTheReplayCommitted() {
        // A replayed database is the source instance's, and the source instance never knew this nonce.
        using var host = Start();
        await MarkInFlightAsync(host, "nonce-abc", "blog", "shop");

        var completion = await CompleteAsync(host);

        Assert.Equal(RestoreOutcome.Succeeded, completion.LastOutcome);
        Assert.Null(completion.LastError);
        var row = await RestoreAuditAsync(host);
        Assert.NotNull(row);
        Assert.True(row.Success);
        Assert.Contains("restored from a bundle taken from 'source'", row.Detail);
    }

    [Fact]
    public async Task TheNonceStillBeingThereMeansTheDatabaseWasNeverReplaced() {
        // The coordinator failed and rolled back, or never got that far. Either way this instance is
        // exactly as it was — which is a failure to report, not a silent no-op.
        using var host = Start();
        await WriteNonceAsync(host, "nonce-abc");
        await MarkInFlightAsync(host, "nonce-abc");

        var completion = await CompleteAsync(host);

        Assert.Equal(RestoreOutcome.Failed, completion.LastOutcome);
        Assert.Contains("running on the database it had", completion.LastError);
        var row = await RestoreAuditAsync(host);
        Assert.NotNull(row);
        Assert.False(row.Success);
        // The marker row is this instance's own litter now, and means nothing in a database that stays.
        Assert.Null(await ReadSettingAsync(host, WatchtowerSettingPaths.RestorePendingNonce));
    }

    [Fact]
    public async Task AFailedRestoreKeepsTheUploadSoItCanBeRetried() {
        using var host = Start();
        var uploadDirectory = Staging(host).NewUploadDirectory();
        await WriteNonceAsync(host, "nonce-abc");
        await MarkInFlightAsync(host, "nonce-abc");

        await CompleteAsync(host);

        Assert.True(Directory.Exists(uploadDirectory));
        // The marker is gone, so a later restart does not re-report the same failure.
        Assert.Null(Staging(host).ReadProgress());
    }

    [Fact]
    public async Task ASucceededRestoreClearsTheBundleAndTheMarker() {
        // The bundle carries every secret the source instance had; once it has been used it is only a
        // copy of the instance lying around in a container.
        using var host = Start();
        await MarkInFlightAsync(host, "nonce-abc");

        await CompleteAsync(host);

        Assert.Null(Staging(host).ReadProgress());
        Assert.Null(Staging(host).Current);
    }

    [Fact]
    public async Task TheBackupCursorsAreClampedSoTheRestoreDoesNotFireAFleetOfBackups() {
        // The restored rows carry the source instance's cursors. Left alone, every window between its
        // dump and now looks missed, and the misfire grace would back up every stack at once — against
        // volumes that have not been redeployed yet.
        using var host = Start();
        var stale = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var stackId = await AddStackAsync(host, "blog", stale);
        await MarkInFlightAsync(host, "nonce-abc");

        await CompleteAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var cursor = await db.Stacks.AsNoTracking()
            .Where(s => s.Id == stackId).Select(s => s.LastScheduledBackupAt).SingleAsync(Ct);
        Assert.NotNull(cursor);
        Assert.True(cursor > stale, "the stack's backup cursor should have been moved to now");
        Assert.NotNull(await ReadSettingAsync(host, WatchtowerSettingPaths.BackupSelfLastScheduledAt));
    }

    [Fact]
    public async Task TheProxyPlaneIsToldToReprojectTheRestoredRoutes() {
        // The routes table arrived wholesale; without the bump the proxy keeps serving what this
        // instance had before the restore.
        using var host = Start();
        var before = await ReadSettingAsync(host, WatchtowerSettingPaths.ProxyRoutesVersion);
        await MarkInFlightAsync(host, "nonce-abc");

        await CompleteAsync(host);

        var after = await ReadSettingAsync(host, WatchtowerSettingPaths.ProxyRoutesVersion);
        Assert.NotNull(after);
        Assert.NotEqual(before, after);
    }

    [Fact]
    public async Task TheRecoveryChecklistIsSeededFromTheRestoredDatabase() {
        // From the database, not from the bundle's manifest: the ids the checklist has to act on are the
        // restored ones.
        using var host = Start();
        var blog = await AddStackAsync(host, "blog", cursor: null);
        var shop = await AddStackAsync(host, "shop", cursor: null);
        await MarkInFlightAsync(host, "nonce-abc", "whatever-the-manifest-said");

        await CompleteAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        var checklist = await StackRevivalState.LoadAsync(settings, Ct);
        Assert.NotNull(checklist);
        Assert.False(checklist.Dismissed);
        Assert.Equal("source", checklist.SourceInstance);
        Assert.Equal([blog, shop], checklist.Stacks.Select(s => s.StackId));
        Assert.All(checklist.Stacks, s => Assert.Equal(RevivalStatus.Pending, s.Status));
    }

    [Fact]
    public async Task AFailedRestoreLeavesNoChecklist() {
        using var host = Start();
        await AddStackAsync(host, "blog", cursor: null);
        await WriteNonceAsync(host, "nonce-abc");
        await MarkInFlightAsync(host, "nonce-abc");

        await CompleteAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<ISettingsManager>();
        Assert.Null(await StackRevivalState.LoadAsync(settings, Ct));
    }
}
