using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The exportable full backup bundle (ADR-0027 §4). What it has to get right is what an import on the
/// other side depends on: every archive present under the storage-relative path the restored database
/// will look for it at, a manifest that says which Watchtower wrote it and against which schema, and
/// the out-of-database secrets without which the restored instance cannot read its own keys.
/// </summary>
public sealed class BackupBundleTests : IDisposable {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _storageRoot = Directory.CreateTempSubdirectory("wt-bundle-tests").FullName;

    public void Dispose() {
        try {
            Directory.Delete(_storageRoot, recursive: true);
        } catch (IOException) {
            // A temp directory the OS will reclaim; never worth failing a passing test over.
        }
    }

    private AuthTestHost Start(params (string, string?)[] more) =>
        AuthTestHost.Start(FakeInstanceBackup.Register, [
            ("Watchtower:Backup:Provider", "local"),
            ("Watchtower:Backup:Local:BasePath", _storageRoot),
            ("Watchtower:Backup:InstanceName", "prod"),
            ("Watchtower:Backup:EncryptionPassphrase", "s3cret"),
            .. more,
        ]);

    private async Task<int> AddStackAsync(AuthTestHost host, string name, string? backupDirectory = null) {
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var stack = new Stack {
            Name = name,
            ComposeProjectName = name,
            Product = TestProducts.New(name),
            BackupDirectory = backupDirectory,
        };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(Ct);
        return stack.Id;
    }

    /// <summary>Puts a stand-in stack archive on the storage, the way a real run would have left one.</summary>
    private async Task<string> SeedArchiveAsync(
        string directory, string stem, DateTimeOffset takenAt, string content) {
        var name = BackupNaming.FileName(stem, takenAt, encrypted: true);
        var path = Path.Combine(_storageRoot, directory.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        await File.WriteAllTextAsync(Path.Combine(path, name), content, Ct);
        return name;
    }

    /// <summary>Runs an export the way the queue would, and returns the staged bundle.</summary>
    private async Task<StagedBundle> ExportAsync(AuthTestHost host) {
        int eventId;
        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var evt = new BackupEvent {
                StackId = null,
                TriggeredBy = BackupTriggers.BundleExport,
                Status = BackupStatuses.Queued,
                StartedAt = DateTimeOffset.UtcNow,
            };
            db.BackupEvents.Add(evt);
            await db.SaveChangesAsync(Ct);
            eventId = evt.Id;
        }

        await host.Services.GetRequiredService<BackupBundleService>().ExecuteExportAsync(eventId, Ct);

        await using (var scope = host.Services.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var evt = await db.BackupEvents.AsNoTracking().SingleAsync(e => e.Id == eventId, Ct);
            Assert.True(
                evt.Status == BackupStatuses.Success,
                $"The export failed. Run log:\n{evt.Output}");
        }

        var staged = host.Services.GetRequiredService<BundleExportState>().Current;
        return Assert.IsType<StagedBundle>(staged);
    }

    /// <summary>Every entry in the tar, by name, with its bytes.</summary>
    private static async Task<Dictionary<string, byte[]>> ReadTarAsync(string path) {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        await using var file = File.OpenRead(path);
        await using var reader = new TarReader(file);
        while (await reader.GetNextEntryAsync() is { } entry) {
            using var content = new MemoryStream();
            if (entry.DataStream is { } data) await data.CopyToAsync(content);
            entries[entry.Name] = content.ToArray();
        }
        return entries;
    }

    private static T Json<T>(Dictionary<string, byte[]> entries, string name) =>
        JsonSerializer.Deserialize<T>(entries[name], BackupBundle.JsonOptions)!;

    [Fact]
    public async Task CarriesTheInstanceArchiveAndEveryStacksNewestOne() {
        using var host = Start();
        await AddStackAsync(host, "blog");
        await AddStackAsync(host, "shop");
        var stale = await SeedArchiveAsync(
            "prod/blog", "blog", new DateTimeOffset(2026, 8, 20, 3, 30, 0, TimeSpan.Zero), "old-blog");
        var newest = await SeedArchiveAsync(
            "prod/blog", "blog", new DateTimeOffset(2026, 8, 25, 3, 30, 0, TimeSpan.Zero), "new-blog");
        await SeedArchiveAsync(
            "prod/shop", "shop", new DateTimeOffset(2026, 8, 25, 3, 31, 0, TimeSpan.Zero), "shop-bytes");

        var staged = await ExportAsync(host);
        var entries = await ReadTarAsync(staged.Path);

        Assert.Equal(2, staged.StackCount);
        Assert.Equal(0, staged.MissingStackCount);
        Assert.Contains(BackupBundle.ManifestEntry, entries.Keys);
        Assert.Contains(BackupBundle.SecretsEntry, entries.Keys);

        // The storage-relative path is preserved under stacks/, so an import can put each archive back
        // exactly where the restored database's BackupDirectory already points.
        Assert.Equal("new-blog", Encoding.UTF8.GetString(entries[$"stacks/prod/blog/{newest}"]));
        Assert.DoesNotContain($"stacks/prod/blog/{stale}", entries.Keys);
        Assert.Equal(
            FakeInstanceBackup.Content,
            Encoding.UTF8.GetString(entries.Single(e => e.Key.StartsWith("watchtower/", StringComparison.Ordinal)).Value));
    }

    [Fact]
    public async Task TheManifestNamesTheBuildTheSchemaAndEveryArchivesDigest() {
        using var host = Start();
        await AddStackAsync(host, "blog");
        await SeedArchiveAsync(
            "prod/blog", "blog", new DateTimeOffset(2026, 8, 25, 3, 30, 0, TimeSpan.Zero), "blog-bytes");

        var entries = await ReadTarAsync((await ExportAsync(host)).Path);
        var manifest = Json<BundleManifest>(entries, BackupBundle.ManifestEntry);

        Assert.Equal(BackupBundle.FormatVersion, manifest.BundleFormatVersion);
        Assert.Equal("watchtower", manifest.Tool);
        Assert.Equal("prod", manifest.InstanceName);
        Assert.False(string.IsNullOrWhiteSpace(manifest.AppVersion));
        // The migration id is what an import decides on, so it has to be the *applied* one, not a guess.
        Assert.False(string.IsNullOrWhiteSpace(manifest.LastMigrationId));

        var stack = Assert.Single(manifest.Stacks);
        Assert.Equal("blog", stack.Name);
        var archive = Assert.IsType<BundleArchive>(stack.Archive);
        Assert.True(archive.Encrypted);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(entries[archive.Entry])),
            archive.Sha256);
        Assert.Equal(entries[archive.Entry].Length, archive.SizeBytes);
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(entries[manifest.Instance.Entry])),
            manifest.Instance.Sha256);
    }

    [Fact]
    public async Task AStackWithNoArchiveIsRecordedRatherThanOmitted() {
        // Its definition still comes back with the database, so the operator has to be told that the
        // data did not — silently listing one stack out of two would read as "there was only one".
        using var host = Start();
        await AddStackAsync(host, "blog");
        await AddStackAsync(host, "never-backed-up");
        await SeedArchiveAsync(
            "prod/blog", "blog", new DateTimeOffset(2026, 8, 25, 3, 30, 0, TimeSpan.Zero), "blog-bytes");

        var staged = await ExportAsync(host);
        var manifest = Json<BundleManifest>(await ReadTarAsync(staged.Path), BackupBundle.ManifestEntry);

        Assert.Equal(1, staged.StackCount);
        Assert.Equal(1, staged.MissingStackCount);
        var missing = Assert.Single(manifest.Stacks, s => s.Name == "never-backed-up");
        Assert.Null(missing.Archive);
        Assert.Equal("no archive on the backup storage", missing.Reason);
    }

    [Fact]
    public async Task ATenantsStampedDirectoryIsHonoured() {
        // BackupDirectory is stamped once and never recomputed, so the bundle has to read it rather than
        // derive a path from the stack's current name — the two differ for every tenant.
        using var host = Start();
        await AddStackAsync(host, "shop-globex", backupDirectory: "prod/shop/globex");
        var name = await SeedArchiveAsync(
            "prod/shop/globex", "shop-globex",
            new DateTimeOffset(2026, 8, 25, 3, 30, 0, TimeSpan.Zero), "globex-bytes");

        var entries = await ReadTarAsync((await ExportAsync(host)).Path);

        Assert.Equal("globex-bytes", Encoding.UTF8.GetString(entries[$"stacks/prod/shop/globex/{name}"]));
    }

    [Fact]
    public async Task TheSecretsFileCarriesWhatTheDatabaseCannot() {
        using var host = Start(("Watchtower:Auth:KeyProtectionSecret", "key-protection-secret"));

        var entries = await ReadTarAsync((await ExportAsync(host)).Path);
        var secrets = Json<BundleSecrets>(entries, BackupBundle.SecretsEntry);

        // Without this one the restored instance throws on every certificate and key it touches.
        Assert.Equal("key-protection-secret", secrets.KeyProtectionSecret);
        Assert.Equal("s3cret", secrets.BackupEncryptionPassphrase);
        Assert.Equal("prod", secrets.BackupInstanceName);
        Assert.Equal("local", secrets.Storage.Provider);
        Assert.Equal(_storageRoot, secrets.Storage.LocalBasePath);

        var manifest = Json<BundleManifest>(entries, BackupBundle.ManifestEntry);
        Assert.True(manifest.KeyProtectionSecretConfigured);
    }

    [Fact]
    public async Task AnInstanceWithNoKeyProtectionSecretSaysSoRatherThanLookingLikeALostOne() {
        using var host = Start();

        var entries = await ReadTarAsync((await ExportAsync(host)).Path);

        Assert.Null(Json<BundleSecrets>(entries, BackupBundle.SecretsEntry).KeyProtectionSecret);
        Assert.False(Json<BundleManifest>(entries, BackupBundle.ManifestEntry).KeyProtectionSecretConfigured);
    }

    [Fact]
    public async Task ASecondExportReplacesTheFirstAndDeletesIt() {
        // One bundle is staged at a time: they are large, and an operator downloading "the" bundle
        // should never be handed a stale one.
        using var host = Start();
        var first = await ExportAsync(host);
        var second = await ExportAsync(host);

        Assert.NotEqual(first.Path, second.Path);
        Assert.False(File.Exists(first.Path));
        Assert.True(File.Exists(second.Path));
    }

    [Fact]
    public async Task TheEventRecordsTheBundleAndTheAuditTrailNamesIt() {
        using var host = Start();
        await AddStackAsync(host, "blog");
        await SeedArchiveAsync(
            "prod/blog", "blog", new DateTimeOffset(2026, 8, 25, 3, 30, 0, TimeSpan.Zero), "blog-bytes");

        var staged = await ExportAsync(host);

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var evt = await db.BackupEvents.AsNoTracking().SingleAsync(Ct);
        Assert.Null(evt.StackId);
        Assert.Equal(staged.SizeBytes, evt.SizeBytes);
        Assert.Equal(staged.FileName, evt.RemotePath);

        var row = await db.AuditEvents.AsNoTracking()
            .SingleAsync(a => a.Action == "bundle.export", Ct);
        Assert.True(row.Success);
        Assert.Contains("1 stack archive(s)", row.Detail);
        // Never the passphrase or the key-protection secret, whatever else the row says.
        Assert.DoesNotContain("s3cret", row.Detail);
    }
}
