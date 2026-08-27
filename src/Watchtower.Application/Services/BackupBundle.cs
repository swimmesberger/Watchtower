using System.Text.Json;
using System.Text.Json.Serialization;

namespace Watchtower.Application.Services;

/// <summary>
/// The layout of a full backup bundle (ADR-0027 §4): one <b>plain</b> tar — its members are already
/// gzipped and encrypted, and its purpose is to be handed to the import on the other side, not to be
/// small — holding a fresh archive of Watchtower's own database, the newest archive of every stack, and
/// the two JSON files that describe them.
/// </summary>
/// <remarks>
/// <code>
/// bundle-manifest.json
/// secrets.json
/// watchtower/watchtower_20260826T033000Z.tar.gz.enc
/// stacks/prod/blog/blog_20260826T033000Z.tar.gz.enc
/// stacks/prod/shop/globex/shop-globex_20260826T033100Z.tar.gz.enc
/// </code>
/// A stack archive keeps its <em>storage-relative</em> path under <c>stacks/</c>, so an import can put
/// it back byte for byte where the restored database already expects to find it
/// (<see cref="Entities.Stack.BackupDirectory"/>) instead of having to rewrite paths it cannot verify.
/// </remarks>
public static class BackupBundle {
    /// <summary>The manifest's entry name inside the tar.</summary>
    public const string ManifestEntry = "bundle-manifest.json";

    /// <summary>The secrets file's entry name inside the tar.</summary>
    public const string SecretsEntry = "secrets.json";

    /// <summary>Directory holding the instance's own archive.</summary>
    public const string InstanceDirectory = "watchtower";

    /// <summary>Directory the stack archives keep their storage-relative paths under.</summary>
    public const string StacksDirectory = "stacks";

    /// <summary>
    /// The bundle format this build writes and reads. Bumped only for a change a previous reader could
    /// not survive; additive keys do not move it.
    /// </summary>
    public const int FormatVersion = 1;

    /// <summary>The file name offered for download, e.g. <c>watchtower-bundle_prod_20260826T033000Z.tar</c>.</summary>
    public static string FileName(string instanceName, DateTimeOffset createdAt) =>
        $"watchtower-bundle_{BackupNaming.Sanitize(instanceName)}_"
        + $"{createdAt.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.tar";

    /// <summary>How the bundle's JSON is written and read: camelCase, nulls kept, indented for a human.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}

/// <summary>One archive inside the bundle, and enough about it to verify and place it.</summary>
/// <param name="Entry">Its path inside the tar.</param>
/// <param name="StoragePath">Where it belongs on the backup storage, relative to the provider base.</param>
/// <param name="SizeBytes">Its size.</param>
/// <param name="Sha256">Lowercase hex digest of the bytes, so an import can prove the tar is intact.</param>
/// <param name="TakenAtUtc">When the archive was made.</param>
/// <param name="Encrypted">Whether it needs the passphrase in <c>secrets.json</c>.</param>
public sealed record BundleArchive(
    string Entry, string StoragePath, long SizeBytes, string Sha256, DateTimeOffset TakenAtUtc, bool Encrypted);

/// <summary>One stack's entry in the bundle manifest: what it is, and which archive belongs to it.</summary>
/// <param name="StackId">The stack's id in the exporting instance's database.</param>
/// <param name="Name">The stack's name, for the operator-facing checklist.</param>
/// <param name="ComposeProject">Its compose project — the identity its volumes carry.</param>
/// <param name="Archive">The archive, or null when the stack had none on the storage.</param>
/// <param name="Reason">Why <paramref name="Archive"/> is null; null when it is not.</param>
public sealed record BundleStack(
    int StackId, string Name, string ComposeProject, BundleArchive? Archive, string? Reason);

/// <summary>
/// <c>bundle-manifest.json</c>: what the bundle holds and which Watchtower wrote it.
/// </summary>
/// <param name="BundleFormatVersion">See <see cref="BackupBundle.FormatVersion"/>.</param>
/// <param name="Tool">Always <c>watchtower</c>, so a stray tar identifies itself.</param>
/// <param name="CreatedAtUtc">When the export ran.</param>
/// <param name="InstanceName">The exporting instance's backup name.</param>
/// <param name="AppVersion">Its build, for the operator-facing half of a version refusal.</param>
/// <param name="LastMigrationId">
/// Its schema. This is what an import <em>decides</em> on: migrations only roll forward, so a bundle
/// whose last migration the target binary has never heard of cannot be replayed into it.
/// </param>
/// <param name="KeyProtectionSecretConfigured">
/// Whether the exporting instance encrypted its stored private keys at all — so an import can tell
/// "no secret was in use" from "the secret is missing from this bundle".
/// </param>
/// <param name="Instance">The archive of Watchtower's own database.</param>
/// <param name="Stacks">Every stack, including those with no archive to carry.</param>
public sealed record BundleManifest(
    int BundleFormatVersion,
    string Tool,
    DateTimeOffset CreatedAtUtc,
    string InstanceName,
    string AppVersion,
    string? LastMigrationId,
    bool KeyProtectionSecretConfigured,
    BundleArchive Instance,
    IReadOnlyList<BundleStack> Stacks);

/// <summary>
/// <c>secrets.json</c>: the material that lives <em>outside</em> the database, without which a restored
/// instance is inert (ADR-0027 §4).
/// </summary>
/// <remarks>
/// Plain text, deliberately. The alternative — a bundle that restores into an instance whose every
/// certificate and key throws because a secret the operator never knew about stayed behind on a machine
/// that no longer exists — is the failure this file exists to prevent. It is why the export is
/// admin-only and audited, and why the UI says what the file is.
/// </remarks>
/// <param name="SecretsFormatVersion">Versioned separately from the manifest; both are read together.</param>
/// <param name="KeyProtectionSecret">
/// <c>Watchtower:Auth:KeyProtectionSecret</c>. The stored certificates, ACME account key and signing key
/// are AES-GCM under it, and it cannot be changed at runtime — a restore checks it before replaying.
/// </param>
/// <param name="BackupEncryptionPassphrase">What decrypts every archive in this bundle.</param>
/// <param name="BackupInstanceName">The instance name the storage layout was written under.</param>
/// <param name="Storage">Where the archives came from, so a restored instance keeps backing up.</param>
public sealed record BundleSecrets(
    int SecretsFormatVersion,
    string? KeyProtectionSecret,
    string? BackupEncryptionPassphrase,
    string? BackupInstanceName,
    BundleStorageSecrets Storage);

/// <summary>The backup storage credentials, as the exporting instance held them.</summary>
public sealed record BundleStorageSecrets(
    string Provider,
    BundleSftpSecrets Sftp,
    string LocalBasePath);

/// <summary>SFTP credentials, whole — a restore that cannot reach the storage cannot revive the stacks.</summary>
public sealed record BundleSftpSecrets(
    string? Host,
    int Port,
    string? Username,
    string? Password,
    string? PrivateKey,
    string? PrivateKeyPassphrase,
    string BasePath);
