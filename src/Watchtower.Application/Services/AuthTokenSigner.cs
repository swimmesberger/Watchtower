using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;

namespace Watchtower.Application.Services;

/// <summary>
/// Mints the short-lived ES256 identity assertion forwarded to a protected app as
/// <c>X-Watchtower-Jwt</c>, and publishes the public half as a JWKS document
/// (docs/central-auth/design.md §2.3, §4).
/// </summary>
/// <remarks>
/// The convenience headers (<c>X-Watchtower-User</c>/<c>-Email</c>) are trustworthy only because every
/// protected site block strips inbound copies before the proxy adds its own. The JWT is the control that
/// does not depend on that topology: an app can verify the signature against the JWKS and check
/// <c>aud</c>, which binds the assertion to <em>its own</em> domain — a token minted for another app is
/// not accepted, so a hostile upstream cannot replay one it received.
/// <para>
/// Lifetime is five minutes: this is an assertion about one proxied request, not a bearer credential the
/// app should store. Rotating the key (deleting the file) therefore invalidates only assertions still in
/// flight.
/// </para>
/// </remarks>
public sealed class AuthTokenSigner : IDisposable {
    /// <summary>File the PEM-encoded private key is persisted as, under <c>Auth:KeyPath</c>.</summary>
    public const string KeyFileName = "jwt-es256.key";

    /// <summary>Issuer used when <c>Auth:Host</c> is not configured.</summary>
    public const string DefaultIssuer = "watchtower";

    /// <summary>How long a minted assertion is valid.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    private readonly IOptionsMonitor<WatchtowerOptions> _options;
    private readonly TimeProvider _time;
    private readonly ILogger<AuthTokenSigner> _logger;

    /// <summary>
    /// Guards both the one-time key load and every signing operation. <see cref="ECDsa"/> makes no
    /// thread-safety guarantee for its instance members, and one ES256 signature costs tens of
    /// microseconds — serialising them is cheaper than reasoning about a shared key handle under
    /// concurrency, at a scale where the proxy is the bottleneck long before this is.
    /// </summary>
    private readonly Lock _gate = new();

    private SigningMaterial? _material;
    private bool _disposed;

    public AuthTokenSigner(
        IOptionsMonitor<WatchtowerOptions> options,
        TimeProvider time,
        ILogger<AuthTokenSigner> logger) {
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>The <c>iss</c> claim: the configured auth host, or <see cref="DefaultIssuer"/>.</summary>
    public string Issuer {
        get {
            var host = _options.CurrentValue.Auth.Host;
            return string.IsNullOrWhiteSpace(host) ? DefaultIssuer : host.Trim();
        }
    }

    /// <summary>RFC 7638 thumbprint of the signing key — the <c>kid</c> in both the JWT header and the JWKS.</summary>
    public string KeyId => Material.KeyId;

    /// <summary>
    /// The JWKS document apps fetch to verify assertions. Contains the public key only; the value is
    /// computed once because it changes only when the key file does, which needs a restart anyway.
    /// </summary>
    public string JwksDocument => Material.Jwks;

    /// <summary>
    /// Mints an assertion for <paramref name="user"/> scoped to <paramref name="routeDomain"/>, which
    /// becomes the <c>aud</c> claim.
    /// </summary>
    public string Mint(User user, string routeDomain) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeDomain);

        var material = Material;
        var now = _time.GetUtcNow();
        var claims = new Dictionary<string, object>(3, StringComparer.Ordinal) {
            // Identity-forwarding claims: the stable key is `sub`; `preferred_username` is the display
            // name and may be renamed by an administrator, so apps must not key records off it.
            ["sub"] = user.Id.ToString(CultureInfo.InvariantCulture),
            ["preferred_username"] = user.UserName,
        };
        if (!string.IsNullOrWhiteSpace(user.Email))
            claims["email"] = user.Email;

        var descriptor = new SecurityTokenDescriptor {
            Issuer = Issuer,
            Audience = routeDomain,
            IssuedAt = now.UtcDateTime,
            Expires = (now + TokenLifetime).UtcDateTime,
            Claims = claims,
            SigningCredentials = material.Credentials,
        };

        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return material.Handler.CreateToken(descriptor);
        }
    }

    /// <summary>Loads (or generates and persists) the key pair on first use.</summary>
    private SigningMaterial Material {
        get {
            var loaded = Volatile.Read(ref _material);
            if (loaded is not null) return loaded;
            lock (_gate) {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_material is not null) return _material;
                var material = SigningMaterial.Load(KeyFilePath, _logger);
                Volatile.Write(ref _material, material);
                return material;
            }
        }
    }

    private string KeyFilePath {
        get {
            var keyPath = _options.CurrentValue.Auth.KeyPath;
            if (string.IsNullOrWhiteSpace(keyPath)) keyPath = new AuthOptions().KeyPath;
            return Path.Combine(keyPath, KeyFileName);
        }
    }

    public void Dispose() {
        lock (_gate) {
            if (_disposed) return;
            _disposed = true;
            _material?.Dispose();
            _material = null;
        }
    }

    /// <summary>The loaded key pair and everything derived from it.</summary>
    private sealed class SigningMaterial(ECDsa key, string keyId, string jwks) : IDisposable {
        public JsonWebTokenHandler Handler { get; } = new();
        public SigningCredentials Credentials { get; } =
            new(new ECDsaSecurityKey(key) { KeyId = keyId }, SecurityAlgorithms.EcdsaSha256);
        public string KeyId { get; } = keyId;
        public string Jwks { get; } = jwks;

        /// <summary>
        /// Reads the PEM at <paramref name="path"/>, generating and persisting one if it is absent.
        /// A key that fails to parse is fatal rather than silently replaced: overwriting it would
        /// invalidate every assertion an operator's apps are pinned to, without anyone noticing.
        /// </summary>
        public static SigningMaterial Load(string path, ILogger logger) {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            try {
                if (!TryPersistNew(path, key, logger)) {
                    key.Dispose();
                    key = ECDsa.Create();
                    key.ImportFromPem(File.ReadAllText(path));
                    logger.LogInformation("Loaded the identity-token signing key from {Path}.", path);
                }

                // The JWKS must never carry `d`: JsonWebKeyConverter emits the private parameters when the
                // key it is handed has them, so the public half is re-exported into its own instance first.
                using var publicOnly = ECDsa.Create();
                publicOnly.ImportParameters(key.ExportParameters(includePrivateParameters: false));
                var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(publicOnly));
                var keyId = Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint());

                return new SigningMaterial(key, keyId, RenderJwks(jwk, keyId));
            } catch {
                key.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Writes a freshly generated key, or reports that one is already there.
        /// <see cref="FileMode.CreateNew"/> rather than an existence check so two starts racing over the
        /// same volume cannot have one silently overwrite the other's key.
        /// </summary>
        private static bool TryPersistNew(string path, ECDsa key, ILogger logger) {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var streamOptions = new FileStreamOptions {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            // Owner-only from the moment the file exists. Windows has no such mode and rejects the
            // option outright, so it is set only where it means something — the shipped image is Linux.
            if (!OperatingSystem.IsWindows())
                streamOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            try {
                using var stream = new FileStream(path, streamOptions);
                using var writer = new StreamWriter(stream);
                writer.Write(key.ExportPkcs8PrivateKeyPem());
            } catch (IOException) when (File.Exists(path)) {
                return false;
            }

            logger.LogInformation("Generated a new ES256 identity-token signing key at {Path}.", path);
            return true;
        }

        /// <summary>
        /// Renders the JWKS by hand rather than serialising <see cref="JsonWebKeySet"/>, which emits empty
        /// <c>key_ops</c>/<c>oth</c>/<c>x5c</c> arrays that some consumers choke on. Every value here is
        /// either a constant or base64url, so there is nothing for the writer to escape.
        /// </summary>
        private static string RenderJwks(JsonWebKey jwk, string keyId) =>
            new JsonObject {
                ["keys"] = new JsonArray(new JsonObject {
                    ["kty"] = jwk.Kty,
                    ["crv"] = jwk.Crv,
                    ["x"] = jwk.X,
                    ["y"] = jwk.Y,
                    ["kid"] = keyId,
                    ["use"] = "sig",
                    ["alg"] = SecurityAlgorithms.EcdsaSha256,
                }),
            }.ToJsonString();

        public void Dispose() => key.Dispose();
    }
}
