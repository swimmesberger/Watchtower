using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services.Yarp;

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
/// The in-process proxy's certificate store — ADR-0022, moved into the database by ADR-0024. Rows in
/// <c>proxy_certificates</c>, and one ready-to-serve <see cref="SslStreamCertificateContext"/> per host
/// in memory, which is what the Kestrel SNI callback hands to <c>SslStream</c> for each handshake.
/// </summary>
/// <remarks>
/// <para>
/// The table is authoritative and the dictionary is a cache of it. That is what makes a second instance
/// possible at all: every node answers the same SNI name with the same certificate, whichever node
/// obtained it, and a node that has just started serves everything rather than only what it ordered
/// itself.
/// </para>
/// <para>
/// Loading is <see cref="InitializeAsync"/>, called from the host's database initialization <em>before</em>
/// Kestrel serves — not from the constructor, which used to read the disk synchronously, and not from a
/// background task. The requirement has not changed, only the source: Kestrel is listening by the time
/// any <c>IHostedService</c> runs, so a store that filled itself later would answer "no certificate" to
/// whatever arrived in the meantime, which a visitor sees as a broken site. What did change is that the
/// store can no longer do that loading in a constructor at all — the read is asynchronous and needs a
/// scope — so the ordering is stated in <c>Program.InitializeDatabaseAsync</c> rather than implied by
/// where the object is built.
/// </para>
/// <para>
/// <see cref="ReloadAsync"/> is the other half: a certificate installed on another instance is a row
/// this one has not read, so the cross-instance change signal (ADR-0024 decision 6) drives a re-read.
/// Only entries whose thumbprint actually moved are rebuilt — building an
/// <see cref="SslStreamCertificateContext"/> costs a PKCS#12 round trip, and the same signal fires for
/// route changes.
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
    /// <summary>The <see cref="KeyProtector"/> purpose the leaf private keys are encrypted under.</summary>
    internal const string KeyPurpose = "proxy-certificate";

    /// <summary>
    /// How far into the future a <c>NotBefore</c> may sit and still be served. Clocks drift, and a CA can
    /// backdate or forward-date by a few seconds; anything beyond this is a certificate that genuinely is
    /// not usable yet, and serving it would produce a browser error rather than a warning in our log.
    /// </summary>
    private static readonly TimeSpan NotBeforeSkew = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Loaded> _certificates = new(StringComparer.Ordinal);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KeyProtector _protector;
    private readonly ProxyChangeSignal _signal;
    private readonly TimeProvider _time;
    private readonly ILogger<CertificateStore> _logger;
    private IDisposable? _watch;
    private bool _disposed;

    public CertificateStore(
        IServiceScopeFactory scopeFactory,
        KeyProtector protector,
        ProxyChangeSignal signal,
        TimeProvider time,
        ILogger<CertificateStore> logger) {
        _scopeFactory = scopeFactory;
        _protector = protector;
        _signal = signal;
        _time = time;
        _logger = logger;
    }

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

    // ── Loading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Fills the cache from the table and starts watching for changes made by other instances. Called
    /// once, from the host's database initialization, before anything can arrive on the HTTPS listener.
    /// </summary>
    /// <remarks>
    /// Never throws for a row it cannot use — one certificate whose key will not decrypt must cost that
    /// one host, not the listener. A database that is unreachable <em>does</em> throw: it is not a
    /// certificate problem, and starting with an empty SNI map because PostgreSQL was down for a second
    /// would fail every handshake for the life of the process.
    /// </remarks>
    public async Task InitializeAsync(CancellationToken ct = default) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var (host, loaded) in await LoadAllAsync(ct)) Activate(host, loaded);
        _logger.LogInformation(
            "Certificate store opened with {Count} certificate(s) from the database.", _certificates.Count);

        // After the first load, so a signal that arrives during startup re-reads a store that is already
        // consistent rather than racing the initial fill.
        _watch ??= _signal.Watch(ReloadAsync);
    }

    /// <summary>
    /// Re-reads the table and brings the cache into line with it — the certificate half of the
    /// cross-instance change signal.
    /// </summary>
    /// <remarks>
    /// An entry whose thumbprint is unchanged is left exactly as it is, including the context object
    /// itself: a signal fires for every route change too, and rebuilding twenty certificate contexts
    /// because somebody renamed a route would be work for nothing. A host whose row is gone is dropped —
    /// another instance deleted it, and continuing to answer for it would make the two disagree.
    /// </remarks>
    public async Task ReloadAsync(CancellationToken ct = default) {
        if (_disposed) return;
        var loaded = await LoadAllAsync(ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (host, candidate) in loaded) {
            seen.Add(host);
            if (_certificates.TryGetValue(host, out var current)
                && string.Equals(
                    current.Entry.Thumbprint, candidate.Entry.Thumbprint, StringComparison.Ordinal)) {
                // Already serving this exact certificate; the freshly built one never becomes visible.
                candidate.Dispose();
                continue;
            }
            if (Activate(host, candidate))
                _logger.LogInformation("Picked up the certificate for {Host} from the database.", host);
        }

        foreach (var host in _certificates.Keys.Where(h => !seen.Contains(h)).ToArray()) {
            // Dropped, not disposed — see the class remarks.
            if (!_certificates.TryRemove(host, out _)) continue;
            _logger.LogInformation("Stopped serving {Host}: its certificate row is gone.", host);
        }
    }

    /// <summary>
    /// Reads every row into servable material. A row that cannot be turned into a certificate is logged
    /// and skipped, exactly as an unreadable directory used to be.
    /// </summary>
    private async Task<List<(string Host, Loaded Loaded)>> LoadAllAsync(CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var rows = await db.ProxyCertificates.AsNoTracking().ToListAsync(ct);

        var loaded = new List<(string, Loaded)>(rows.Count);
        foreach (var row in rows) {
            try {
                loaded.Add((row.Host, Materialize(row)));
            } catch (Exception ex) {
                _logger.LogWarning(
                    ex, "Skipping the certificate for {Host}: its row could not be loaded.", row.Host);
            }
        }
        return loaded;
    }

    // ── Mutations ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores a newly obtained certificate and starts serving it. Row first, memory second: a crash
    /// between the two costs one restart's worth of staleness, whereas the other order would serve a
    /// certificate that is not persisted anywhere.
    /// </summary>
    /// <param name="pemChain">The issued chain, leaf first, as concatenated PEM blocks.</param>
    /// <param name="privateKey">The key the leaf was issued for. Not taken ownership of.</param>
    public async Task InstallAsync(string host, string pemChain, ECDsa privateKey, CancellationToken ct) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(pemChain);
        ArgumentNullException.ThrowIfNull(privateKey);
        var name = NormalizeHost(host);

        // Parse before writing, so material that could never be served does not reach the table at all.
        var described = DescribeChain(name, pemChain);
        await UpsertAsync(
            name, pemChain, privateKey.ExportPkcs8PrivateKeyPem(), described,
            ProxyCertificateSources.Acme, ct);

        // Re-read from the row rather than from the arguments: what is served is then provably what a
        // restart would load, instead of two code paths that can disagree.
        var stored = await ReadAsync(name, ct)
                     ?? throw new InvalidOperationException(
                         $"The certificate for {name} was written but could not be read back.");
        var loaded = Materialize(stored);
        if (!Activate(name, loaded))
            _logger.LogWarning(
                "The certificate installed for {Host} is not valid before {NotBefore:u}; it was stored "
                + "but is not being served yet.", name, loaded.Entry.NotBefore);

        await _signal.BumpAsync($"certificate installed for {name}", ct);
    }

    /// <summary>
    /// Stores a certificate carried in from the pre-ADR-0024 volume. Differs from
    /// <see cref="InstallAsync"/> only in what it records as the source, in taking the key as PEM — which
    /// is the form the file had — and in leaving an existing row alone: a certificate already in the
    /// table was issued after the upgrade, and is newer than anything on the old volume.
    /// </summary>
    /// <returns>Whether a row was written.</returns>
    internal async Task<bool> ImportAsync(
        string host, string pemChain, string keyPem, CancellationToken ct) {
        var name = NormalizeHost(host);
        var described = DescribeChain(name, pemChain);
        return await UpsertAsync(
            name, pemChain, keyPem, described, ProxyCertificateSources.FileImport, ct, onlyIfAbsent: true);
    }

    /// <summary>Parses a PEM chain into the summary columns, and refuses one with no certificate in it.</summary>
    private static CertificateEntry DescribeChain(string host, string pemChain) {
        var parsed = new X509Certificate2Collection();
        parsed.ImportFromPem(pemChain);
        if (parsed.Count == 0)
            throw new ArgumentException("The PEM chain contains no certificate.", nameof(pemChain));
        try {
            return Describe(host, parsed[0], parsed.Count);
        } finally {
            foreach (var certificate in parsed) certificate.Dispose();
        }
    }

    /// <summary>Writes the row, replacing whatever was there for the host.</summary>
    /// <param name="onlyIfAbsent">The import's mode: leave an existing row alone.</param>
    /// <returns>Whether a row was written.</returns>
    private async Task<bool> UpsertAsync(
        string host,
        string pemChain,
        string keyPem,
        CertificateEntry described,
        string source,
        CancellationToken ct,
        bool onlyIfAbsent = false) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        var existing = await db.ProxyCertificates.FirstOrDefaultAsync(c => c.Host == host, ct);
        if (existing is not null && onlyIfAbsent) return false;

        var protectedKey = _protector.ProtectText(keyPem, KeyPurpose);
        var now = _time.GetUtcNow();
        if (existing is null) {
            db.ProxyCertificates.Add(new ProxyCertificate {
                Host = host,
                CertificatePem = pemChain,
                PrivateKey = protectedKey,
                Protection = _protector.CurrentProtection,
                NotBefore = described.NotBefore,
                NotAfter = described.NotAfter,
                Issuer = described.IssuerCommonName,
                Thumbprint = described.Thumbprint,
                Source = source,
                InstalledAt = now,
            });
        } else {
            existing.CertificatePem = pemChain;
            existing.PrivateKey = protectedKey;
            // Every write re-protects, which is how a row stored before the secret was configured stops
            // being plaintext without a migration pass over the whole table.
            existing.Protection = _protector.CurrentProtection;
            existing.NotBefore = described.NotBefore;
            existing.NotAfter = described.NotAfter;
            existing.Issuer = described.IssuerCommonName;
            existing.Thumbprint = described.Thumbprint;
            existing.Source = source;
            existing.InstalledAt = now;
        }

        try {
            await db.SaveChangesAsync(ct);
        } catch (DbUpdateException ex) when (existing is null && IsUniqueViolation(ex)) {
            // Two instances finished an order for the same host at once — the issuer lease makes this
            // unlikely rather than impossible (a handover mid-order is exactly the window). Whichever row
            // landed first is a perfectly good certificate for the same name, so this one steps aside
            // rather than fighting for the last word.
            _logger.LogInformation(
                "Another instance stored a certificate for {Host} first; keeping theirs.", host);
            return false;
        } catch (DbUpdateConcurrencyException) {
            // The same race one moment later: the row moved between the read above and this write, so
            // the xmin token no longer matches. Same verdict for the same reason — both writers hold a
            // valid certificate for the host, and the one that landed is as good as this one. The caller
            // re-reads, so what it goes on to serve is the winner's row rather than its own arguments.
            _logger.LogInformation(
                "Another instance replaced the certificate for {Host} while this one was storing it; "
                + "keeping theirs.", host);
            return false;
        }
        return true;
    }

    /// <summary>
    /// Drops what is held for a host and deletes its row. This is the "the domain is gone" path — a host
    /// merely dropping out of the desired set keeps its certificate, or a route removed by mistake would
    /// cost a fresh issuance against the CA's rate limits.
    /// </summary>
    /// <returns>Whether anything was actually removed.</returns>
    public async Task<bool> ForgetAsync(string host, CancellationToken ct = default) {
        string name;
        try {
            name = NormalizeHost(host);
        } catch (ArgumentException) {
            // Nothing can be stored under a name the store would refuse to write, so there is nothing
            // to remove — and a caller cleaning up after bad input deserves an answer, not a throw.
            return false;
        }

        // Dropped, not disposed: a handshake that picked this context up a moment ago still holds the
        // leaf it was removed by. Unreferenced, the handle is reclaimed on its own.
        var removed = _certificates.TryRemove(name, out _);

        int deleted;
        await using (var scope = _scopeFactory.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            deleted = await db.ProxyCertificates.Where(c => c.Host == name).ExecuteDeleteAsync(ct);
        }

        if (!removed && deleted == 0) return false;
        await _signal.BumpAsync($"certificate removed for {name}", ct);
        return true;
    }

    /// <summary>
    /// Deletes certificates for hosts nothing wants any more, once they have been expired for
    /// <paramref name="grace"/>. Deliberately conservative on both axes — the host has to be gone from
    /// the desired set <em>and</em> the certificate has to be past its usefulness — because the cost of
    /// deleting one that is still wanted is a new issuance against a rate limit.
    /// </summary>
    /// <remarks>
    /// Driven off the rows rather than off the cache, so a certificate this instance never loaded (one
    /// whose key will not decrypt, say) is still eligible: the prune is housekeeping on the table, and a
    /// row nobody can serve is exactly the kind of thing that should not accumulate.
    /// </remarks>
    /// <returns>How many hosts were removed.</returns>
    public async Task<int> PruneUndesiredAsync(
        IReadOnlySet<string> desired, TimeSpan grace, CancellationToken ct = default) {
        ArgumentNullException.ThrowIfNull(desired);
        var wanted = new HashSet<string>(desired, StringComparer.OrdinalIgnoreCase);
        var cutoff = _time.GetUtcNow() - grace;

        List<string> candidates;
        await using (var scope = _scopeFactory.CreateAsyncScope()) {
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            candidates = await db.ProxyCertificates.AsNoTracking()
                .Where(c => c.NotAfter < cutoff)
                .Select(c => c.Host)
                .ToListAsync(ct);
        }

        var removed = 0;
        foreach (var host in candidates) {
            if (wanted.Contains(host)) continue;
            if (!await ForgetAsync(host, ct)) continue;
            removed++;
            _logger.LogInformation(
                "Removed the expired certificate for {Host}; nothing routes to it any more.", host);
        }
        return removed;
    }

    // ── Materialization ───────────────────────────────────────────────────────

    private async Task<ProxyCertificate?> ReadAsync(string host, CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.ProxyCertificates.AsNoTracking().FirstOrDefaultAsync(c => c.Host == host, ct);
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
            _logger.LogWarning(
                "Not serving the certificate for {Host}: it is not valid before {NotBefore:u}.",
                host, loaded.Entry.NotBefore);
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
    /// Turns one row into servable material. Throws on anything unreadable — the callers decide whether
    /// that is fatal (an install) or a row to skip (the load).
    /// </summary>
    private Loaded Materialize(ProxyCertificate row) {
        // X509Certificate2.CreateFromPem keeps only the first certificate, which would silently drop
        // exactly the intermediates this store exists to send.
        var chain = new X509Certificate2Collection();
        chain.ImportFromPem(row.CertificatePem);
        if (chain.Count == 0)
            throw new CryptographicException($"The stored chain for {row.Host} contains no certificate.");

        var leaf = chain[0];
        var intermediates = new X509Certificate2Collection();
        for (var i = 1; i < chain.Count; i++) intermediates.Add(chain[i]);

        X509Certificate2? withKey = null;
        X509Certificate2? usable = null;
        var keyPem = _protector.Unprotect(row.PrivateKey, row.Protection, KeyPurpose);
        try {
            withKey = Rekey(leaf, Encoding.UTF8.GetString(keyPem));

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
            return new Loaded(usable, intermediates, context, Describe(row.Host, usable, chain.Count));
        } catch {
            usable?.Dispose();
            foreach (var intermediate in intermediates) intermediate.Dispose();
            throw;
        } finally {
            CryptographicOperations.ZeroMemory(keyPem);
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

    /// <summary>
    /// The name a host's material is stored under. Validates rather than sanitises: every caller passes a
    /// host name that has already been normalised, so anything else here is a bug or an injection
    /// attempt, and quietly rewriting it into <em>some</em> row is the outcome worth ruling out.
    /// </summary>
    /// <exception cref="ArgumentException">The argument is not a plain lowercase-able DNS name.</exception>
    public static string NormalizeHost(string host) {
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

    /// <summary>
    /// Whether a failed save was the unique index on <c>host</c> rejecting a second row — PostgreSQL's
    /// <c>23505</c>, reached through EF's wrapper.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _watch?.Dispose();
        _watch = null;
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
