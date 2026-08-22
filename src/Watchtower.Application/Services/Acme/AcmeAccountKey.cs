using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services.Acme;

/// <summary>
/// The ES256 key pair every ACME request is signed with, and the account URL the CA issued for it —
/// ADR-0017 (forthcoming). Persisted under <c>{CertPath}/accounts/{directory hash}</c> so an account
/// survives restarts and, more importantly, so re-registering is never necessary: an ACME account is
/// rate-limited per key, and a deployment that regenerated one on every start would exhaust its budget.
/// </summary>
/// <remarks>
/// The layout deliberately mirrors <see cref="AuthTokenSigner"/>'s: PKCS#8 PEM, created with
/// <see cref="FileMode.CreateNew"/> so two starts racing over the same volume cannot have one overwrite
/// the other's key, owner-only permissions from the moment the file exists, and a parse failure that
/// throws rather than silently regenerating. That last one matters more here than there — quietly
/// replacing an account key means the CA no longer associates this deployment with its issuance history,
/// including the rate-limit allowances an established account has earned.
/// <para>
/// The directory is keyed by the ACME directory URL by the caller, not by this class. An account exists
/// only at the CA that issued it, so switching from Let's Encrypt to an internal CA has to produce a
/// fresh key and a fresh registration rather than presenting one CA's account URL to another.
/// </para>
/// </remarks>
public sealed class AcmeAccountKey : IDisposable {
    /// <summary>The PKCS#8 PEM private key.</summary>
    public const string KeyFileName = "account.key";

    /// <summary>The registration bookkeeping: which directory, and the account URL it issued.</summary>
    public const string AccountFileName = "account.json";

    private readonly string _accountFilePath;
    private readonly string _directoryUrl;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private string? _accountUrl;
    private bool _disposed;

    private AcmeAccountKey(ECDsa key, string accountFilePath, string directoryUrl, string? accountUrl, ILogger logger) {
        Key = key;
        _accountFilePath = accountFilePath;
        _directoryUrl = directoryUrl;
        _accountUrl = accountUrl;
        _logger = logger;
    }

    /// <summary>The signing key. Owned by this instance and disposed with it.</summary>
    public ECDsa Key { get; }

    /// <summary>
    /// The registered account URL (the JWS <c>kid</c>), or null when this key has never been registered
    /// — or when the CA has since forgotten it.
    /// </summary>
    public string? AccountUrl {
        get { lock (_gate) return _accountUrl; }
    }

    /// <summary>
    /// Loads the key pair for one ACME directory, generating and persisting one on first use.
    /// </summary>
    /// <param name="accountDirectory">
    /// The per-directory folder, which the caller derives from the directory URL — see the class remarks.
    /// </param>
    /// <param name="directoryUrl">
    /// The ACME directory this account belongs to. Recorded in <c>account.json</c>, and a stored account
    /// URL from a <em>different</em> directory is discarded rather than presented to the wrong CA.
    /// </param>
    /// <exception cref="CryptographicException">An existing key file could not be parsed.</exception>
    public static AcmeAccountKey Load(string accountDirectory, string directoryUrl, ILogger logger) {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryUrl);
        ArgumentNullException.ThrowIfNull(logger);

        Directory.CreateDirectory(accountDirectory);
        var keyPath = Path.Combine(accountDirectory, KeyFileName);
        var accountPath = Path.Combine(accountDirectory, AccountFileName);

        // Generate only when there is nothing to load. The existence check is not the race guard —
        // FileMode.CreateNew inside TryPersistNew is — it is what stops an ordinary restart minting a
        // P-256 key pair it is about to throw away.
        if (!File.Exists(keyPath)) {
            var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            try {
                if (TryPersistNew(keyPath, generated, logger))
                    // Brand new key: whatever account.json may hold belongs to a key that is gone.
                    return new AcmeAccountKey(generated, accountPath, directoryUrl, accountUrl: null, logger);
            } catch {
                generated.Dispose();
                throw;
            }
            // Lost the race with another start over the same volume; load the key it wrote.
            generated.Dispose();
        }

        var key = ECDsa.Create();
        try {
            // Fatal, deliberately: a key file that will not parse is a corrupted volume or a hand-edited
            // file, and overwriting it would silently abandon the account the CA knows this deployment
            // as — including whatever rate-limit allowance it has earned. Re-thrown as a
            // CryptographicException because ImportFromPem reports a malformed PEM as an
            // ArgumentException about its parameter, which describes the call rather than the problem.
            try {
                key.ImportFromPem(File.ReadAllText(keyPath));
            } catch (Exception ex) when (ex is ArgumentException or CryptographicException) {
                throw new CryptographicException(
                    $"The ACME account key at {keyPath} could not be read. Move it aside to register a new "
                    + "account, or restore the volume it lives on.", ex);
            }
            logger.LogInformation("Loaded the ACME account key from {Path}.", keyPath);
            return new AcmeAccountKey(
                key, accountPath, directoryUrl, ReadAccountUrl(accountPath, directoryUrl, logger), logger);
        } catch {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Records the account URL the CA issued, so the next start signs with <c>kid</c> straight away.</summary>
    public void SetAccountUrl(string url) {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        lock (_gate) {
            if (string.Equals(_accountUrl, url, StringComparison.Ordinal)) return;
            _accountUrl = url;
            Persist();
        }
    }

    /// <summary>
    /// Forgets the account URL — the <c>accountDoesNotExist</c> path, where the CA no longer knows the
    /// account this key was registered as and the only way forward is to register it again.
    /// </summary>
    public void ClearAccountUrl() {
        lock (_gate) {
            if (_accountUrl is null) return;
            _accountUrl = null;
            Persist();
        }
    }

    /// <summary>
    /// Writes <c>account.json</c> temp-and-move, so a crash mid-write cannot leave a truncated file that
    /// the next start would read as "never registered" and re-register against.
    /// </summary>
    private void Persist() {
        try {
            var json = JsonSerializer.Serialize(
                new AcmeAccountFile { DirectoryUrl = _directoryUrl, AccountUrl = _accountUrl },
                AcmeJsonContext.Default.AcmeAccountFile);
            var temporary = _accountFilePath + ".tmp";
            File.WriteAllText(temporary, json);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _accountFilePath, overwrite: true);
        } catch (Exception ex) {
            // Not fatal: the account still exists at the CA and the in-memory URL is correct for this
            // process. The cost of losing it is one extra registration on the next start, which the CA
            // answers with the same account URL for the same key (RFC 8555 §7.3).
            _logger.LogWarning(ex, "Could not persist the ACME account registration to {Path}.", _accountFilePath);
        }
    }

    /// <summary>
    /// Reads the stored account URL, ignoring one that was issued by a different directory. Never
    /// throws: an unreadable or malformed file means "not registered here", which costs a registration
    /// rather than an outage.
    /// </summary>
    private static string? ReadAccountUrl(string path, string directoryUrl, ILogger logger) {
        if (!File.Exists(path)) return null;
        try {
            var stored = JsonSerializer.Deserialize(
                File.ReadAllText(path), AcmeJsonContext.Default.AcmeAccountFile);
            if (stored is null) return null;
            if (!string.Equals(stored.DirectoryUrl, directoryUrl, StringComparison.OrdinalIgnoreCase)) {
                logger.LogWarning(
                    "Ignoring the stored ACME account at {Path}: it was registered with {Stored}, not {Configured}.",
                    path, stored.DirectoryUrl, directoryUrl);
                return null;
            }
            return string.IsNullOrWhiteSpace(stored.AccountUrl) ? null : stored.AccountUrl;
        } catch (Exception ex) {
            logger.LogWarning(ex, "Could not read the ACME account registration at {Path}; re-registering.", path);
            return null;
        }
    }

    /// <summary>
    /// Writes a freshly generated key, or reports that one is already there. Lifted from
    /// <see cref="AuthTokenSigner"/> for the same reason it exists there: <see cref="FileMode.CreateNew"/>
    /// rather than an existence check, so two starts over one volume cannot both think they created it.
    /// </summary>
    private static bool TryPersistNew(string path, ECDsa key, ILogger logger) {
        var streamOptions = new FileStreamOptions {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
            streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try {
            using var stream = new FileStream(path, streamOptions);
            using var writer = new StreamWriter(stream);
            writer.Write(key.ExportPkcs8PrivateKeyPem());
        } catch (IOException) when (File.Exists(path)) {
            return false;
        }

        logger.LogInformation("Generated a new ES256 ACME account key at {Path}.", path);
        return true;
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        Key.Dispose();
    }
}
