using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Watchtower.Application.Services;

namespace Watchtower.Application.Tests;

/// <summary>
/// Builds full backup bundles in memory, so the restore's refusals can be tested one at a time against
/// a bundle that is correct in every other respect.
/// </summary>
/// <remarks>
/// The archives are assembled here rather than produced by a real backup run: the format is what the
/// restore reads, so writing it directly is the honest way to test reading it — and a real run would
/// need a Docker daemon and a second PostgreSQL to produce one byte of it.
/// </remarks>
internal static class TestBundles {
    /// <summary>The passphrase every bundle this class builds is encrypted with.</summary>
    public const string Passphrase = "bundle-passphrase";

    /// <summary>What a valid instance archive's dump file is called inside it.</summary>
    private const string DumpFile = "_dumps/watchtower.sql";

    /// <summary>
    /// An encrypted, gzipped instance archive — the real layout: a manifest and a dump under
    /// <c>backup/</c>.
    /// </summary>
    /// <param name="sql">The dump's content. Its bytes are never parsed, only carried.</param>
    /// <param name="withDump">False writes an archive carrying no dump at all.</param>
    public static byte[] InstanceArchive(string sql = "-- pg_dumpall output", bool withDump = true) {
        var manifest = InstanceBackupService.BuildManifest(
            "source", new SelfPostgresTarget(
                "abc", "watchtower-postgres-1", "postgres:18-alpine", "postgres", "watchtower", "watchtower"),
            DateTimeOffset.UtcNow,
            new BackupService.BackupDumpEntry(
                "watchtower", DumpEngine.Postgres, DumpFile, "postgres:18-alpine", "watchtower",
                "watchtower-postgres-1", [], ["watchtower"], sql.Length),
            // The archive's *own* manifest. Nothing reads this id — the restore decides on the bundle
            // manifest's, which Build takes from the caller — so it is deliberately not a real one.
            lastMigrationId: "00000000000000_TestArchive");

        using var plain = new MemoryStream();
        using (var gzip = new GZipStream(plain, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true)) {
            WriteEntry(writer, "backup/backup-manifest.json", Encoding.UTF8.GetBytes(manifest));
            if (withDump) WriteEntry(writer, $"backup/{DumpFile}", Encoding.UTF8.GetBytes(sql));
        }

        using var encrypted = new MemoryStream();
        var cipher = BackupEncryption.CreateEncryptingStream(encrypted, Passphrase);
        plain.Position = 0;
        plain.CopyTo(cipher);
        cipher.Dispose();
        return encrypted.ToArray();
    }

    /// <summary>How one bundle differs from the valid one, for a test that pins a single refusal.</summary>
    /// <param name="LastMigrationId">Null keeps the migration this build knows.</param>
    /// <param name="KeyProtectionSecret">The secret the source instance's keys are under.</param>
    /// <param name="BundleFormatVersion">Null keeps the current format version.</param>
    /// <param name="CorruptInstanceDigest">Records a checksum the archive does not have.</param>
    /// <param name="OmitInstanceArchive">Lists the archive in the manifest but leaves it out of the tar.</param>
    /// <param name="WrongPassphrase">Encrypts the archive with a passphrase the secrets file does not name.</param>
    /// <param name="WithoutDump">Writes an instance archive that carries no dump.</param>
    /// <param name="Stacks">Stack names to carry archives for.</param>
    /// <param name="MissingStacks">Stack names to describe with no archive.</param>
    internal sealed record Options(
        string? LastMigrationId = null,
        string? KeyProtectionSecret = null,
        int? BundleFormatVersion = null,
        bool CorruptInstanceDigest = false,
        bool OmitInstanceArchive = false,
        bool WrongPassphrase = false,
        bool WithoutDump = false,
        IReadOnlyList<string>? Stacks = null,
        IReadOnlyList<string>? MissingStacks = null);

    /// <summary>Builds one bundle tar.</summary>
    /// <param name="lastMigrationId">The migration this build actually knows, so the default is valid.</param>
    /// <param name="options">How this bundle should differ from a valid one.</param>
    public static byte[] Build(string? lastMigrationId, Options? options = null) {
        var o = options ?? new Options();
        var instanceBytes = o.WrongPassphrase
            ? Reencrypt(InstanceArchive(withDump: !o.WithoutDump), "a-different-passphrase")
            : InstanceArchive(withDump: !o.WithoutDump);
        var instanceEntry = $"{BackupBundle.InstanceDirectory}/watchtower_20260826T033000Z.tar.gz.enc";
        var instance = new BundleArchive(
            instanceEntry, "source/_watchtower/watchtower_20260826T033000Z.tar.gz.enc",
            instanceBytes.Length,
            o.CorruptInstanceDigest ? new string('0', 64) : Sha256(instanceBytes),
            DateTimeOffset.UtcNow, Encrypted: true);

        var members = new List<(string Entry, byte[] Content)>();
        if (!o.OmitInstanceArchive) members.Add((instanceEntry, instanceBytes));

        var stacks = new List<BundleStack>();
        foreach (var (name, index) in (o.Stacks ?? []).Select((n, i) => (n, i))) {
            var content = Encoding.UTF8.GetBytes($"archive-for-{name}");
            var storagePath = $"source/{name}/{name}_2026082{index}T033000Z.tar.gz.enc";
            var entry = $"{BackupBundle.StacksDirectory}/{storagePath}";
            members.Add((entry, content));
            stacks.Add(new BundleStack(
                index + 1, name, name,
                new BundleArchive(
                    entry, storagePath, content.Length, Sha256(content), DateTimeOffset.UtcNow, true),
                null));
        }
        foreach (var (name, index) in (o.MissingStacks ?? []).Select((n, i) => (n, i)))
            stacks.Add(new BundleStack(
                100 + index, name, name, null, "no archive on the backup storage"));

        var manifest = new BundleManifest(
            o.BundleFormatVersion ?? BackupBundle.FormatVersion,
            "watchtower",
            DateTimeOffset.UtcNow,
            "source",
            "9.9.9-test",
            o.LastMigrationId ?? lastMigrationId,
            KeyProtectionSecretConfigured: o.KeyProtectionSecret is { Length: > 0 },
            instance,
            stacks);

        var secrets = new BundleSecrets(
            SecretsFormatVersion: 1,
            KeyProtectionSecret: o.KeyProtectionSecret,
            BackupEncryptionPassphrase: Passphrase,
            BackupInstanceName: "source",
            Storage: new BundleStorageSecrets(
                "local", new BundleSftpSecrets(null, 22, null, null, null, null, ""), "/backups"));

        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true)) {
            WriteEntry(writer, BackupBundle.ManifestEntry, Json(manifest));
            WriteEntry(writer, BackupBundle.SecretsEntry, Json(secrets));
            foreach (var (entry, content) in members) WriteEntry(writer, entry, content);
        }
        return tar.ToArray();
    }

    /// <summary>
    /// A tar whose entry name would escape the directory it is unpacked into. Both shapes are here: a
    /// leading <c>../</c>, and one buried after a legitimate-looking segment — the second is what a
    /// prefix-stripping guard would miss.
    /// </summary>
    public static byte[] TraversalBundle(string entryName = "stacks/../../escaped.json") {
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
            WriteEntry(writer, entryName, Encoding.UTF8.GetBytes("{}"));
        return tar.ToArray();
    }

    /// <summary>A tar with no manifest in it at all.</summary>
    public static byte[] NotABundle() {
        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
            WriteEntry(writer, "readme.txt", Encoding.UTF8.GetBytes("not a bundle"));
        return tar.ToArray();
    }

    private static byte[] Reencrypt(byte[] archive, string passphrase) {
        // Decrypt with the class passphrase and re-encrypt with another, so the bytes are a real
        // archive that simply cannot be opened with what the secrets file names.
        using var source = new MemoryStream(archive);
        using var plain = new MemoryStream();
        var decrypting = BackupEncryption.CreateDecryptingStream(source, Passphrase);
        decrypting.CopyTo(plain);
        decrypting.Dispose();

        using var result = new MemoryStream();
        var cipher = BackupEncryption.CreateEncryptingStream(result, passphrase);
        plain.Position = 0;
        plain.CopyTo(cipher);
        cipher.Dispose();
        return result.ToArray();
    }

    private static void WriteEntry(TarWriter writer, string name, byte[] content) {
        using var data = new MemoryStream(content);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) {
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            DataStream = data,
        });
    }

    private static byte[] Json<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, BackupBundle.JsonOptions);

    private static string Sha256(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));
}
