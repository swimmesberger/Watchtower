using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services.Acme;

/// <summary>What is known about one host's certificate, without handing out the certificate itself.</summary>
/// <param name="ChainLength">Leaf plus intermediates — 1 means nothing but the leaf was stored.</param>
public sealed record CertificateEntry(
    string Host,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string IssuerCommonName,
    string Thumbprint,
    int ChainLength);

/// <summary>
/// The in-process proxy's certificate store — ADR-0020. PEM files on disk under
/// <see cref="RootPath"/>, and one ready-to-serve <see cref="SslStreamCertificateContext"/> per host in
/// memory, which is what the Kestrel SNI callback hands to <c>SslStream</c> for each handshake.
/// </summary>
/// <remarks>
/// <para>
/// The constructor loads everything <em>synchronously</em>, and that is a requirement rather than a
/// convenience: Kestrel is already listening by the time the first <c>IHostedService</c> runs, so a store
/// that filled itself from a background task would answer "no certificate" to whatever arrived in the
/// meantime — a handshake failure the client sees as a broken site. The store is resolved while Kestrel
/// is being configured, so by the time a connection can arrive the map is populated.
/// </para>
/// <para>
/// A certificate context is built once per install and reused for every handshake, with the whole chain
/// baked in via <see cref="SslStreamCertificateContext.Create(X509Certificate2, X509Certificate2Collection?, bool)"/>.
/// Kestrel's simpler "server certificate selector" path hands <c>SslStream</c> the bare leaf, which then
/// builds a chain itself from the machine's trust store — on a container that ships no intermediates that
/// silently produces handshakes missing the issuer certificate, which many clients reject. Sending the
/// chain we were issued is the only way to be sure.
/// </para>
/// <para>
/// Reads are lock-free (a <see cref="ConcurrentDictionary{TKey,TValue}"/> lookup per handshake) because
/// they happen on the connection path. A certificate that has been <em>published</em> is never disposed
/// on the way out — a renewal replaces the entry and a delete drops it, but both simply let go of the
/// reference. <see cref="SslStreamCertificateContext"/> keeps the caller's leaf instance as its
/// <c>TargetCertificate</c> (only the intermediates are cloned), so disposing it would pull the key out
/// from under any handshake still holding that context. The <see cref="SafeHandle"/> behind it is
/// reclaimed once nothing references it, which is exactly the "when the last handshake is done"
/// condition we would otherwise have to invent a grace period for. Material that was never published —
/// a load that failed, a certificate that is not valid yet — is disposed eagerly, and so is everything
/// left in the map when the store itself is disposed at shutdown.
/// </para>
/// </remarks>
public sealed class CertificateStore : IDisposable {
    private const string CertFileName = "cert.pem";
    private const string KeyFileName = "key.pem";
    private const string MetaFileName = "meta.json";

    /// <summary>
    /// How far into the future a <c>NotBefore</c> may sit and still be served. Clocks drift, and a CA can
    /// backdate or forward-date by a few seconds; anything beyond this is a certificate that genuinely is
    /// not usable yet, and serving it would produce a browser error rather than a warning in our log.
    /// </summary>
    private static readonly TimeSpan NotBeforeSkew = TimeSpan.FromMinutes(5);

    private static readonly UnixFileMode PublicFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private static readonly UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly ConcurrentDictionary<string, Loaded> _certificates = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly ILogger<CertificateStore> _logger;
    private bool _disposed;

    public CertificateStore(
        IOptionsMonitor<WatchtowerOptions> options, TimeProvider time, ILogger<CertificateStore> logger) {
        _time = time;
        _logger = logger;
        var configured = options.CurrentValue.Proxy.Yarp.CertPath;
        RootPath = string.IsNullOrWhiteSpace(configured) ? new YarpProxyOptions().CertPath : configured.Trim();
        LoadAll();
    }

    /// <summary>
    /// The directory the certificates live under, read once at construction. <c>Proxy:Yarp:CertPath</c> is
    /// bind-time configuration for exactly this reason — the store is opened over it, so a change would
    /// need a restart anyway.
    /// </summary>
    public string RootPath { get; }

    /// <summary>What the store currently serves, in no particular order.</summary>
    public IReadOnlyCollection<CertificateEntry> Entries =>
        _certificates.Values.Select(l => l.Entry).ToArray();

    // ── The handshake path ────────────────────────────────────────────────────

    /// <summary>
    /// The certificate context for an SNI name, or <see langword="null"/> when nothing is held for it —
    /// which fails the handshake, deliberately: a connection for a host we have no certificate for must
    /// not be answered with some other host's certificate.
    /// </summary>
    /// <remarks>
    /// Exact match only. A wildcard certificate would have to be looked up by parent domain, and nothing
    /// issues one today; adding that lookup before there is anything to find would be a guess about how
    /// wildcards get stored.
    /// </remarks>
    public SslStreamCertificateContext? SelectContext(string? sni) => Lookup(sni)?.Context;

    /// <summary>The leaf certificate for an SNI name — the same lookup, without the chain.</summary>
    public X509Certificate2? SelectCertificate(string? sni) => Lookup(sni)?.Leaf;

    /// <summary>What is known about a host's certificate, or <see langword="null"/> if none is held.</summary>
    public CertificateEntry? Find(string host) => Lookup(host)?.Entry;

    private Loaded? Lookup(string? host) {
        if (string.IsNullOrWhiteSpace(host)) return null;
        // A trailing dot is the fully-qualified form of the same name, and a client is entitled to send
        // it in SNI; the store never writes one, so it is stripped rather than looked up.
        var name = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (name.Length == 0) return null;
        return _certificates.TryGetValue(name, out var loaded) ? loaded : null;
    }

    // ── Mutations ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a newly obtained certificate to disk and starts serving it. Files first, memory second: a
    /// crash between the two costs one restart's worth of staleness, whereas the other order would serve
    /// a certificate that is not persisted anywhere.
    /// </summary>
    /// <param name="pemChain">The issued chain, leaf first, as concatenated PEM blocks.</param>
    /// <param name="privateKey">The key the leaf was issued for. Not taken ownership of.</param>
    public async Task InstallAsync(string host, string pemChain, ECDsa privateKey, CancellationToken ct) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(pemChain);
        ArgumentNullException.ThrowIfNull(privateKey);
        var name = HostDirectoryName(host);

        // Parse before writing, so material that could never be served does not reach the disk at all.
        var parsed = new X509Certificate2Collection();
        parsed.ImportFromPem(pemChain);
        if (parsed.Count == 0)
            throw new ArgumentException("The PEM chain contains no certificate.", nameof(pemChain));
        foreach (var certificate in parsed) certificate.Dispose();

        var directory = Path.Combine(RootPath, name);
        Directory.CreateDirectory(directory);
        await WriteAtomicAsync(Path.Combine(directory, CertFileName), pemChain, PublicFileMode, ct);
        await WriteAtomicAsync(
            Path.Combine(directory, KeyFileName), privateKey.ExportPkcs8PrivateKeyPem(), PrivateFileMode, ct);

        // Re-read from disk rather than from the arguments: what is served is then provably what a
        // restart would load, instead of two code paths that can disagree.
        var loaded = LoadFromDisk(name, directory);
        try {
            await WriteAtomicAsync(
                Path.Combine(directory, MetaFileName), RenderMeta(loaded.Entry), PublicFileMode, ct);
        } catch (Exception ex) {
            // Pure convenience for an operator reading the volume; nothing reads it back.
            _logger.LogWarning(ex, "Could not write {File} for {Host}.", MetaFileName, name);
        }

        if (!Activate(name, loaded))
            _logger.LogWarning(
                "The certificate installed for {Host} is not valid before {NotBefore:u}; it was written to "
                + "disk but is not being served yet.", name, loaded.Entry.NotBefore);
    }

    /// <summary>
    /// Drops what is held for a host. Only the route-delete path passes
    /// <paramref name="deleteFiles"/>: a host merely dropping out of the desired set must keep its files,
    /// or a route removed by mistake would cost a fresh issuance against the CA's rate limits.
    /// </summary>
    /// <returns>Whether anything was actually removed.</returns>
    public bool Forget(string host, bool deleteFiles) {
        string name;
        try {
            name = HostDirectoryName(host);
        } catch (ArgumentException) {
            // Nothing can be stored under a name the store would refuse to write, so there is nothing
            // to remove — and a caller cleaning up after bad input deserves an answer, not a throw.
            return false;
        }

        // Dropped, not disposed: a handshake that picked this context up a moment ago still holds the
        // leaf it was removed by. Unreferenced, the handle is reclaimed on its own.
        var removed = _certificates.TryRemove(name, out _);

        if (!deleteFiles) return removed;
        var directory = Path.Combine(RootPath, name);
        if (!Directory.Exists(directory)) return removed;
        try {
            Directory.Delete(directory, recursive: true);
            return true;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Could not delete the certificate directory for {Host}.", name);
            return removed;
        }
    }

    /// <summary>
    /// Deletes certificates for hosts nothing wants any more, once they have been expired for
    /// <paramref name="grace"/>. Deliberately conservative on both axes — the host has to be gone from
    /// the desired set <em>and</em> the certificate has to be past its usefulness — because the cost of
    /// deleting one that is still wanted is a new issuance against a rate limit.
    /// </summary>
    /// <returns>How many hosts were removed.</returns>
    public int PruneUndesired(IReadOnlySet<string> desired, TimeSpan grace) {
        ArgumentNullException.ThrowIfNull(desired);
        var wanted = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);
        var now = _time.GetUtcNow();
        var removed = 0;
        foreach (var entry in Entries) {
            if (wanted.Contains(entry.Host)) continue;
            if (entry.NotAfter + grace >= now) continue;
            if (!Forget(entry.Host, deleteFiles: true)) continue;
            removed++;
            _logger.LogInformation(
                "Removed the expired certificate for {Host}; nothing routes to it any more.", entry.Host);
        }
        return removed;
    }

    // ── Loading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans <see cref="RootPath"/> once. Never throws: a store that refused to construct because one
    /// directory on a mounted volume is unreadable would take the whole host down with it, and the rest
    /// of the certificates are still perfectly serviceable.
    /// </summary>
    private void LoadAll() {
        if (!Directory.Exists(RootPath)) {
            _logger.LogInformation(
                "No certificate directory at {CertPath} yet; the proxy starts with nothing to serve.", RootPath);
            return;
        }

        string[] directories;
        try {
            // Materialised inside the try on purpose: EnumerateDirectories is lazy, so a volume the
            // process cannot read would otherwise throw out of the foreach — and out of the constructor,
            // taking the whole host down over a directory nobody has put a certificate in yet.
            directories = Directory.GetDirectories(RootPath);
        } catch (Exception ex) {
            _logger.LogWarning(
                ex, "Could not read the certificate directory {CertPath}; the proxy starts with nothing to serve.",
                RootPath);
            return;
        }

        foreach (var directory in directories) {
            var candidate = Path.GetFileName(directory);
            string name;
            try {
                name = HostDirectoryName(candidate);
            } catch (ArgumentException) {
                _logger.LogWarning(
                    "Ignoring {Directory} under {CertPath}: it is not a host name.", candidate, RootPath);
                continue;
            }

            if (!File.Exists(Path.Combine(directory, CertFileName))) continue;

            Loaded loaded;
            try {
                loaded = LoadFromDisk(name, directory);
            } catch (Exception ex) {
                _logger.LogWarning(
                    ex, "Skipping the certificate for {Host}: it could not be loaded from {Directory}.",
                    name, directory);
                continue;
            }

            if (!Activate(name, loaded))
                _logger.LogWarning(
                    "Not serving the certificate for {Host}: it is not valid before {NotBefore:u}.",
                    name, loaded.Entry.NotBefore);
        }

        _logger.LogInformation(
            "Certificate store opened at {CertPath} with {Count} certificate(s).", RootPath, _certificates.Count);
    }

    /// <summary>
    /// Publishes a loaded certificate, unless it is not usable yet. An expired one <em>is</em> published:
    /// serving a certificate a browser will complain about beats refusing the handshake outright, which
    /// looks to a visitor like the site is down.
    /// </summary>
    /// <remarks>
    /// One atomic swap, and the certificate it replaces is dropped rather than disposed: the context a
    /// handshake is already using holds that very leaf instance. <c>AddOrUpdate</c> rather than the
    /// indexer so two installs racing over one host cannot interleave into a lost or doubly-released
    /// entry — the loser is simply overwritten. Only material that never became visible is disposed here.
    /// </remarks>
    private bool Activate(string host, Loaded loaded) {
        if (loaded.Entry.NotBefore > _time.GetUtcNow() + NotBeforeSkew) {
            loaded.Dispose();
            return false;
        }

        if (loaded.Entry.NotAfter < _time.GetUtcNow())
            _logger.LogWarning(
                "The certificate for {Host} expired at {NotAfter:u}; serving it anyway until it is renewed.",
                host, loaded.Entry.NotAfter);

        _certificates.AddOrUpdate(host, loaded, (_, _) => loaded);
        return true;
    }

    /// <summary>
    /// Reads one host directory into a servable certificate. Throws on anything unreadable — the callers
    /// decide whether that is fatal (an install) or a directory to skip (the startup scan).
    /// </summary>
    private Loaded LoadFromDisk(string host, string directory) {
        // X509Certificate2.CreateFromPemFile keeps only the first certificate, which would silently drop
        // exactly the intermediates this store exists to send.
        var chain = new X509Certificate2Collection();
        chain.ImportFromPemFile(Path.Combine(directory, CertFileName));
        if (chain.Count == 0)
            throw new CryptographicException($"{CertFileName} contains no certificate.");

        var leaf = chain[0];
        var intermediates = new X509Certificate2Collection();
        for (var i = 1; i < chain.Count; i++) intermediates.Add(chain[i]);

        X509Certificate2? withKey = null;
        X509Certificate2? usable = null;
        try {
            var keyPem = File.ReadAllText(Path.Combine(directory, KeyFileName));
            withKey = Rekey(leaf, keyPem);

            // A private key attached with CopyWithPrivateKey is not always usable for TLS on every
            // platform (the key can be ephemeral, or live outside a provider SslStream can reach), so the
            // pair is round-tripped through PKCS#12, which is the form every platform imports natively.
            var pfx = withKey.Export(X509ContentType.Pkcs12);
            try {
                usable = X509CertificateLoader.LoadPkcs12(pfx, password: null, KeyStorageFlags);
            } finally {
                CryptographicOperations.ZeroMemory(pfx);
            }

            var context = SslStreamCertificateContext.Create(usable, intermediates, offline: true);
            return new Loaded(usable, intermediates, context, Describe(host, usable, chain.Count));
        } catch {
            usable?.Dispose();
            foreach (var intermediate in intermediates) intermediate.Dispose();
            throw;
        } finally {
            // The leaf without the key and the intermediate instance holding it are both scratch: what is
            // served is the PKCS#12 round-trip.
            leaf.Dispose();
            withKey?.Dispose();
        }
    }

    /// <summary>Attaches the PEM-encoded private key to the leaf, whichever algorithm it is.</summary>
    private static X509Certificate2 Rekey(X509Certificate2 leaf, string keyPem) {
        try {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(keyPem);
            return leaf.CopyWithPrivateKey(ecdsa);
        } catch (Exception ex) when (ex is CryptographicException or ArgumentException) {
            // ACME issues EC keys by default, but an operator can hand-place an RSA pair, and an internal
            // CA may only issue RSA at all.
            using var rsa = RSA.Create();
            rsa.ImportFromPem(keyPem);
            return leaf.CopyWithPrivateKey(rsa);
        }
    }

    /// <summary>
    /// macOS rejects <see cref="X509KeyStorageFlags.EphemeralKeySet"/> outright, so the one platform that
    /// cannot have it gets the default; the shipped image is Linux, where the key never touches disk.
    /// </summary>
    private static X509KeyStorageFlags KeyStorageFlags =>
        OperatingSystem.IsMacOS() ? X509KeyStorageFlags.DefaultKeySet : X509KeyStorageFlags.EphemeralKeySet;

    private static CertificateEntry Describe(string host, X509Certificate2 leaf, int chainLength) => new(
        Host: host,
        NotBefore: leaf.NotBefore.ToUniversalTime(),
        NotAfter: leaf.NotAfter.ToUniversalTime(),
        IssuerCommonName: leaf.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
        Thumbprint: leaf.Thumbprint,
        ChainLength: chainLength);

    // ── Files ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The directory a host's material lives in. Validates rather than sanitises: every caller passes a
    /// host name that has already been normalised, so anything else here is a bug or an injection
    /// attempt, and quietly rewriting it into <em>some</em> directory is the outcome worth ruling out.
    /// </summary>
    /// <exception cref="ArgumentException">The argument is not a plain lowercase-able DNS name.</exception>
    public static string HostDirectoryName(string host) {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var name = host.ToLowerInvariant();
        if (name.Length > 253)
            throw new ArgumentException($"'{host}' is longer than a DNS name may be.", nameof(host));

        var labels = name.Split('.');
        foreach (var label in labels) {
            if (label.Length is 0 or > 63)
                throw new ArgumentException($"'{host}' has an empty or over-long label.", nameof(host));
            if (label[0] == '-' || label[^1] == '-')
                throw new ArgumentException($"'{host}' has a label starting or ending with '-'.", nameof(host));
            foreach (var c in label)
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                    throw new ArgumentException(
                        $"'{host}' is not a plain ASCII DNS name (letters, digits, '-' and '.').", nameof(host));
        }

        return name;
    }

    private static async Task WriteAtomicAsync(string path, string content, UnixFileMode mode, CancellationToken ct) {
        var temporary = path + ".tmp";
        var options = new FileStreamOptions {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        // Windows has no such mode and rejects the option outright; the shipped image is Linux.
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = mode;

        await using (var stream = new FileStream(temporary, options)) {
            await using (var writer = new StreamWriter(stream, leaveOpen: true)) {
                await writer.WriteAsync(content.AsMemory(), ct);
            }
            // The rename below is atomic, but only against content that has actually reached the device:
            // without this, a power loss right after the move can leave a present-but-empty cert.pem,
            // which is worse than the absent file the store knows how to skip.
            stream.Flush(flushToDisk: true);
        }

        // Again, explicitly: UnixCreateMode only applies when the file is created, and the temporary may
        // have survived an interrupted write with whatever mode that one left behind.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, mode);
        File.Move(temporary, path, overwrite: true);
    }

    private string RenderMeta(CertificateEntry entry) => JsonSerializer.Serialize(
        new CertificateMetadata(
            entry.Host, entry.NotBefore, entry.NotAfter, entry.IssuerCommonName, entry.Thumbprint,
            _time.GetUtcNow()),
        CertificateStoreJsonContext.Default.CertificateMetadata);

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        foreach (var loaded in _certificates.Values) loaded.Dispose();
        _certificates.Clear();
    }

    /// <summary>
    /// One host's servable material: the leaf, the context handed to SslStream, and the summary.
    /// </summary>
    /// <remarks>
    /// Disposing this is only correct for material nothing has been handed yet, or at shutdown —
    /// <see cref="Context"/> holds <see cref="Leaf"/> itself as its target, so releasing it mid-flight
    /// would fail a handshake that is already under way. Removing an entry from the map therefore just
    /// drops the reference and leaves the handle to the finalizer.
    /// </remarks>
    private sealed class Loaded(
        X509Certificate2 leaf,
        X509Certificate2Collection intermediates,
        SslStreamCertificateContext context,
        CertificateEntry entry) : IDisposable {
        public X509Certificate2 Leaf { get; } = leaf;
        public SslStreamCertificateContext Context { get; } = context;
        public CertificateEntry Entry { get; } = entry;

        public void Dispose() {
            Leaf.Dispose();
            foreach (var intermediate in intermediates) intermediate.Dispose();
        }
    }
}

/// <summary>
/// <c>meta.json</c> — what an operator looking at the volume needs to answer "which certificate is this
/// and when does it expire" without running <c>openssl</c>. Written on install and never read back: the
/// store always derives its state from the certificate itself, so the file cannot go stale in a way that
/// matters.
/// </summary>
internal sealed record CertificateMetadata(
    string Host,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Issuer,
    string Thumbprint,
    DateTimeOffset InstalledAt);

/// <summary>Source-generated serializer for <see cref="CertificateMetadata"/>.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CertificateMetadata))]
internal sealed partial class CertificateStoreJsonContext : JsonSerializerContext;
