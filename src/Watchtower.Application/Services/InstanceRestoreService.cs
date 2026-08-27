using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Takes an uploaded full backup bundle, decides whether this instance can restore it, and — once an
/// admin confirms — replaces this instance's database with the one inside it (ADR-0027 §5).
/// </summary>
/// <remarks>
/// <para>
/// The replay cannot be done by this process. <c>pg_dumpall --clean</c> terminates every session and
/// drops every database, and Watchtower's own connection pool would reconnect straight into the middle
/// of that. So everything that <em>can</em> be done from here is done first — validating, re-uploading
/// the stack archives to storage, pushing the SQL into the database container, writing the marker — and
/// then a sibling coordinator container stops Watchtower, replays, and starts it again.
/// </para>
/// <para>
/// Every refusal happens before any of that. An instance that cannot read the bundle it was given must
/// still be the instance it was.
/// </para>
/// </remarks>
public sealed class InstanceRestoreService(
    DockerEngineClient docker,
    PostgresDumpService postgres,
    SelfPostgresLocator locator,
    BackupStorageFactory storageFactory,
    InstanceRestoreStaging staging,
    SelfUpdateService selfUpdate,
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<WatchtowerOptions> options,
    AuditLog audit,
    ILogger<InstanceRestoreService> logger) {
    /// <summary>How the restore names itself in the audit trail.</summary>
    internal const string AuditTarget = "watchtower (instance)";

    /// <summary>
    /// Unpacks and checks an uploaded bundle, publishing it as the staged restore when it is at least
    /// readable. A bundle with blocking findings is still staged, so the UI can show what is wrong
    /// rather than only that something was.
    /// </summary>
    /// <param name="tar">The uploaded bundle.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">The upload is not a Watchtower bundle at all.</exception>
    public async Task<RestoreValidation> StageAsync(Stream tar, CancellationToken ct) {
        var directory = staging.NewUploadDirectory();
        Dictionary<string, string> digests;
        try {
            digests = await InstanceRestoreStaging.ExtractAsync(tar, directory, ct);
        } catch {
            try {
                Directory.Delete(directory, recursive: true);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Could not delete a partially extracted bundle at {Directory}", directory);
            }
            throw;
        }

        var manifest = ReadJson<BundleManifest>(directory, BackupBundle.ManifestEntry)
            ?? throw new InvalidOperationException(
                $"The upload has no {BackupBundle.ManifestEntry} — it is not a Watchtower backup bundle.");
        var secrets = ReadJson<BundleSecrets>(directory, BackupBundle.SecretsEntry)
            ?? throw new InvalidOperationException(
                $"The bundle has no {BackupBundle.SecretsEntry}.");

        var staged = new StagedRestore(directory, manifest, secrets, DateTimeOffset.UtcNow);
        staging.Replace(staged);
        return await ValidateAsync(staged, digests, ct);
    }

    /// <summary>Re-checks the staged bundle — the UI reads this on load, when nothing was just uploaded.</summary>
    public Task<RestoreValidation> ValidateAsync(StagedRestore staged, CancellationToken ct) =>
        ValidateAsync(staged, digests: null, ct);

    /// <param name="digests">
    /// SHA-256 by entry name from the extraction, when this call follows one. Null on a re-check, where
    /// re-hashing a multi-gigabyte bundle to answer a page load would be the wrong trade — the digests
    /// were checked when it arrived.
    /// </param>
    private async Task<RestoreValidation> ValidateAsync(
        StagedRestore staged, Dictionary<string, string>? digests, CancellationToken ct) {
        var manifest = staged.Manifest;
        var blocking = new List<RestoreFinding>();
        var warnings = new List<RestoreFinding>();

        if (manifest.BundleFormatVersion != BackupBundle.FormatVersion)
            blocking.Add(new RestoreFinding("bundle-format",
                $"This bundle is format version {manifest.BundleFormatVersion}, and this Watchtower reads "
                + $"version {BackupBundle.FormatVersion}."));

        // Migrations only roll forward, so the question is not "which version is newer" but "does this
        // binary know that schema" — which is exact.
        using (var scope = scopeFactory.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            if (!InstanceVersion.Knows(db, manifest.LastMigrationId))
                blocking.Add(new RestoreFinding("newer-schema",
                    $"The bundle was written by Watchtower {manifest.AppVersion}, whose database schema "
                    + "this build does not know. Update this Watchtower to that version or newer, then "
                    + "restore — a database only ever migrates forward."));
        }

        // The sharpest edge in the whole feature: the stored certificates, ACME account key and signing
        // key are AES-GCM under this secret, and it cannot be changed at runtime.
        var current = options.CurrentValue.Auth.KeyProtectionSecret;
        var fromBundle = staged.Secrets.KeyProtectionSecret;
        if (!string.IsNullOrEmpty(fromBundle) && !string.Equals(fromBundle, current, StringComparison.Ordinal))
            blocking.Add(new RestoreFinding("key-protection-secret",
                "The bundle's private keys are encrypted with a key-protection secret this instance does "
                + "not have, so every certificate and signing key in it would be unreadable here. Set "
                + "WATCHTOWER__AUTH__KEYPROTECTIONSECRET to the value in the bundle's secrets.json and "
                + "restart Watchtower, then restore — it cannot be changed while running."));
        else if (string.IsNullOrEmpty(fromBundle) && !string.IsNullOrEmpty(current))
            warnings.Add(new RestoreFinding("key-protection-secret-new",
                "The bundle's keys are stored unencrypted, and this instance has a key-protection secret "
                + "configured. The restored keys stay readable, and anything written afterwards is "
                + "encrypted — nothing is lost, but the two halves of the database differ."));

        // Every archive present and intact, checked before rather than after the database is gone.
        foreach (var archive in Archives(manifest)) {
            var path = staged.PathOf(archive);
            if (!File.Exists(path)) {
                blocking.Add(new RestoreFinding("missing-archive",
                    $"The bundle's manifest lists '{archive.Entry}', which is not in the file. It is "
                    + "incomplete or was repacked."));
                continue;
            }
            if (digests is not null
                && digests.TryGetValue(archive.Entry, out var actual)
                && !string.Equals(actual, archive.Sha256, StringComparison.OrdinalIgnoreCase))
                blocking.Add(new RestoreFinding("corrupt-archive",
                    $"'{archive.Entry}' does not match the checksum in the manifest — the bundle was "
                    + "damaged in transit or altered."));
        }

        // Proves the passphrase and the archive together, without touching anything.
        if (blocking.Count == 0) {
            try {
                var contents = await BackupArchiveReader.ReadContentsAsync(
                    staged.PathOf(manifest.Instance),
                    manifest.Instance.Encrypted ? staged.Secrets.BackupEncryptionPassphrase : null, ct);
                if (contents.DumpFiles.Count == 0)
                    blocking.Add(new RestoreFinding("no-dump",
                        "The bundle's Watchtower archive carries no database dump, so there is nothing "
                        + "to restore from."));
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                blocking.Add(new RestoreFinding("unreadable-archive",
                    $"The bundle's Watchtower archive could not be read: {ex.Message}"));
            }
        }

        // A restore needs the database as a container to exec into, exactly as a self-backup does.
        try {
            await locator.LocateAsync(_ => { }, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            blocking.Add(new RestoreFinding("no-database-container", ex.Message));
        }

        if (!await IsFreshAsync(ct))
            warnings.Add(new RestoreFinding("not-fresh",
                "This Watchtower already manages stacks. Restoring replaces its entire database — the "
                + "stacks, accounts and settings it has now are gone, and the containers they deployed "
                + "keep running unmanaged."));

        var stacks = manifest.Stacks;
        return new RestoreValidation(
            CanRestore: blocking.Count == 0,
            Blocking: blocking,
            Warnings: warnings,
            InstanceName: manifest.InstanceName,
            AppVersion: manifest.AppVersion,
            CreatedAtUtc: manifest.CreatedAtUtc,
            StackCount: stacks.Count(s => s.Archive is not null),
            MissingStackCount: stacks.Count(s => s.Archive is null),
            StackNames: [.. stacks.Select(s => s.Name)]);
    }

    /// <summary>
    /// Puts everything in place and hands over to the coordinator. Returns once the coordinator has been
    /// started — from the caller's point of view Watchtower is about to stop answering.
    /// </summary>
    /// <param name="actor">Who asked, for the audit row written before the database goes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Nothing staged, validation refuses, or the pre-stage fails.</exception>
    public async Task StartAsync(string? actor, CancellationToken ct) {
        var staged = staging.Current
            ?? throw new InvalidOperationException("No bundle has been uploaded.");
        var validation = await ValidateAsync(staged, ct);
        if (!validation.CanRestore)
            throw new InvalidOperationException(
                "This bundle cannot be restored into this instance: "
                + string.Join(" ", validation.Blocking.Select(b => b.Message)));

        var self = await selfUpdate.DetectSelfAsync(ct);
        if (self.ContainerId is not { Length: > 0 } selfContainerId)
            throw new InvalidOperationException(
                "Watchtower is not running as a container on this Docker daemon, so it cannot be stopped "
                + "and restarted around the replay. Restore the dump by hand — see docs/backups.md.");

        var manifest = staged.Manifest;
        var passphrase = staged.Secrets.BackupEncryptionPassphrase;

        // 1. The stack archives go back to the storage first, at the paths the restored database will
        // look for them at, so the recovery checklist has something to restore from afterwards.
        var backup = options.CurrentValue.Backup;
        using (var storage = storageFactory.Create(backup)) {
            foreach (var stack in manifest.Stacks) {
                if (stack.Archive is not { } archive) continue;
                var source = staged.PathOf(archive);
                await storage.UploadAsync(archive.StoragePath, async (destination, token) => {
                    await using var file = File.OpenRead(source);
                    await file.CopyToAsync(destination, token);
                }, ct);
            }
        }

        // 2. The SQL is pushed into the database container now, while this process still has a working
        // Docker client and the archive's passphrase — the coordinator has neither.
        var target = await locator.LocateAsync(_ => { }, ct);
        var connection = await postgres.PreflightAsync(target.ToDumpTarget(), _ => { }, ct);
        var sqlSpool = Path.Combine(Path.GetTempPath(), $"watchtower-restore-{Guid.NewGuid():N}.sql");
        try {
            var dumpFile = await ResolveDumpFileAsync(staged, passphrase, ct);
            await BackupArchiveReader.ExtractAsync(
                staged.PathOf(manifest.Instance),
                manifest.Instance.Encrypted ? passphrase : null, dumpFile, sqlSpool, ct);
            await PushSqlAsync(target.ContainerId, sqlSpool, ct);
        } finally {
            try {
                if (File.Exists(sqlSpool)) File.Delete(sqlSpool);
            } catch (Exception ex) {
                logger.LogWarning(ex, "Failed to delete the restore SQL spool {SpoolPath}", sqlSpool);
            }
        }

        // 3. The nonce goes into the database that is about to be replaced. Its absence afterwards is
        // what proves the replay committed; nothing else can remove it.
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        using (var scope = scopeFactory.CreateScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            await db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO elarion_settings (kind, owner, key, value, version, updated_at)
                VALUES ('global', '', {0}, {1}, 1, now())
                ON CONFLICT (kind, owner, key) DO UPDATE SET value = EXCLUDED.value
                """,
                [WatchtowerSettingPaths.RestorePendingNonce, nonce], ct);
        }

        await staging.WriteProgressAsync(
            new RestoreProgress(
                nonce, DateTimeOffset.UtcNow, manifest.InstanceName, CoordinatorId: null,
                [.. manifest.Stacks.Select(s => s.Name)]),
            ct);

        // Written before the coordinator starts: after this point the database that would hold the row
        // is replaced, so an audit row written later would be written into a database that never saw
        // the decision.
        await audit.RecordAsync(
            BackupService.AuditCategory, "instance.restore", AuditTarget,
            $"restoring from a bundle taken from '{manifest.InstanceName}' ({manifest.AppVersion}, "
            + $"{manifest.CreatedAtUtc:u}) — {validation.StackCount} stack archive(s)",
            actor: actor, ct: CancellationToken.None);

        // 4. Hand over. From here the coordinator owns the outcome.
        var coordinatorId = await SpawnCoordinatorAsync(
            self.ImageName ?? throw new InvalidOperationException(
                "Watchtower's own image could not be determined, so no coordinator can be started from it."),
            selfContainerId, target.ContainerId, connection, ct);

        await staging.WriteProgressAsync(
            new RestoreProgress(
                nonce, DateTimeOffset.UtcNow, manifest.InstanceName, coordinatorId,
                [.. manifest.Stacks.Select(s => s.Name)]),
            ct);
        logger.LogWarning(
            "Instance restore handed to coordinator {CoordinatorId}; this process will be stopped shortly",
            coordinatorId);
    }

    /// <summary>The dump's path inside the archive, from the archive's own manifest.</summary>
    private static async Task<string> ResolveDumpFileAsync(
        StagedRestore staged, string? passphrase, CancellationToken ct) {
        var contents = await BackupArchiveReader.ReadContentsAsync(
            staged.PathOf(staged.Manifest.Instance),
            staged.Manifest.Instance.Encrypted ? passphrase : null, ct);
        return contents.DumpFiles.FirstOrDefault()
            ?? throw new InvalidOperationException(
                "The bundle's Watchtower archive carries no database dump.");
    }

    /// <summary>
    /// Copies the SQL into the database container at <see cref="InstanceRestoreStaging.RemoteSqlPath"/>,
    /// 0600 — it carries every role's password hash.
    /// </summary>
    private async Task PushSqlAsync(string containerId, string sqlPath, CancellationToken ct) {
        var directory = Path.GetDirectoryName(InstanceRestoreStaging.RemoteSqlPath)!.Replace('\\', '/');
        var name = Path.GetFileName(InstanceRestoreStaging.RemoteSqlPath);
        // PutContainerArchive will not create the parent, so it is made first.
        var mkdir = await docker.ExecAsync(containerId, ["mkdir", "-p", directory], ct: ct);
        if (!mkdir.Success)
            throw new InvalidOperationException(
                $"Could not create {directory} inside the database container "
                + $"(exit code {mkdir.ExitCode}): {PostgresDumpService.Tail(mkdir.Stderr)}");

        await docker.PutContainerArchiveAsync(containerId, directory, async (stream, token) => {
            await using var writer = new TarWriter(stream, TarEntryFormat.Pax, leaveOpen: true);
            await using var content = File.OpenRead(sqlPath);
            await writer.WriteEntryAsync(
                new PaxTarEntry(TarEntryType.RegularFile, name) {
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    DataStream = content,
                }, token);
        }, ct);
    }

    /// <summary>
    /// Starts the sibling container that does the replay. Same shape as the self-update coordinator:
    /// Watchtower's own image so it runs the code it was built with, the Docker socket, no network, and
    /// the process's supplementary groups (<see cref="HostSupplementaryGroups"/>) so it can use the
    /// socket at all — the third consumer of that rule, after the self-update coordinator and the CI
    /// runner containers.
    /// </summary>
    private async Task<string> SpawnCoordinatorAsync(
        string imageName, string selfContainerId, string postgresContainerId,
        PostgresConnection connection, CancellationToken ct) {
        var name = $"watchtower-restore-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        string[] command = [
            "--restore-self",
            "--container-id", selfContainerId,
            "--postgres-id", postgresContainerId,
            "--sql", InstanceRestoreStaging.RemoteSqlPath,
            "--db-user", connection.User,
            .. connection.ExecUser is { Length: > 0 } execUser ? (string[])["--db-exec-user", execUser] : [],
            .. connection.Databases.SelectMany(d => new[] { "--expect-db", d }),
        ];
        List<string> env = [$"WATCHTOWER__DOCKERAPIVERSION={options.CurrentValue.DockerApiVersion}"];
        // Only when there is one. It is visible in `docker inspect` of the coordinator, which is
        // accepted: reading that needs the Docker socket, and anyone holding it owns the host anyway.
        if (connection.Password is { Length: > 0 } password)
            env.Add($"{RestoreCoordinatorEnvironment.PostgresPassword}={password}");

        var coordinatorId = await docker.CreateContainerAsync(new DockerCreateContainerBody {
            Image = imageName,
            Cmd = command,
            Env = [.. env],
            HostConfig = new DockerCreateHostConfig {
                Binds = ["/var/run/docker.sock:/var/run/docker.sock"],
                NetworkMode = "none",
                GroupAdd = HostSupplementaryGroups.Current(),
            },
        }, name, ct);
        await docker.StartContainerAsync(coordinatorId, ct);
        return coordinatorId;
    }

    /// <summary>
    /// Whether this looks like a Watchtower nobody has used yet: no stacks, no deploys, and one account.
    /// A heuristic for a warning, never for a permission — the restore is gated on being an admin.
    /// </summary>
    public async Task<bool> IsFreshAsync(CancellationToken ct) {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return !await db.Stacks.AnyAsync(ct)
            && !await db.DeployEvents.AnyAsync(ct)
            && await db.Users.CountAsync(u => u.RealmId == Entities.Realm.SystemRealmId, ct) <= 1;
    }

    /// <summary>Every archive the manifest promises, instance and stacks alike.</summary>
    private static IEnumerable<BundleArchive> Archives(BundleManifest manifest) =>
        [manifest.Instance, .. manifest.Stacks.Select(s => s.Archive).OfType<BundleArchive>()];

    private static T? ReadJson<T>(string directory, string entry) {
        var path = Path.Combine(directory, entry);
        if (!File.Exists(path)) return default;
        try {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), BackupBundle.JsonOptions);
        } catch (JsonException ex) {
            throw new InvalidOperationException($"The bundle's {entry} could not be read: {ex.Message}");
        }
    }
}
