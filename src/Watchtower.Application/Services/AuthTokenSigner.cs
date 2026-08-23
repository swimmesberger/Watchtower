using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Everything <see cref="AuthTokenSigner"/> needs to know about a realm, as a value — which is what keeps
/// the signer a pure singleton with no database of its own (docs/central-auth/design.md §13). Callers that
/// have a <see cref="Realm"/> project it with <see cref="From"/>; the rest of the signer never learns that
/// realms are rows.
/// </summary>
/// <param name="Slug">The realm's stable identifier, which becomes the <c>realm</c> claim.</param>
/// <param name="LoginHost">Its login host, which becomes the <c>iss</c> claim for a non-system realm.</param>
/// <param name="IsSystem">True for the operator realm, whose issuer is the instance-wide configured one.</param>
public readonly record struct RealmIdentity(string Slug, string? LoginHost, bool IsSystem) {
    /// <summary>The built-in operator realm — the realm every assertion belonged to before realms existed.</summary>
    public static RealmIdentity System { get; } = new(Realm.SystemRealmSlug, null, IsSystem: true);

    /// <summary>
    /// Projects a stored realm together with the login host resolved for it
    /// (<see cref="RealmResolver.LoginHostForAsync"/>). The host is passed in rather than read off the row
    /// because since ADR-0023 it lives on the realm's login <see cref="Route"/>, and the signer is a
    /// singleton with no database of its own.
    /// </summary>
    public static RealmIdentity From(Realm realm, string? loginHost) {
        ArgumentNullException.ThrowIfNull(realm);
        return new RealmIdentity(realm.Slug, loginHost, realm.IsSystem);
    }
}

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
/// app should store. Rotating the key (deleting the <c>signing_keys</c> row) therefore invalidates only
/// assertions still in flight.
/// </para>
/// <para>
/// The key pair is a row rather than a file since ADR-0024, which is what makes an assertion minted on
/// one instance verifiable on every other: all of them read the same key, publish the same JWKS and
/// stamp the same <c>kid</c>.
/// </para>
/// </remarks>
public sealed class AuthTokenSigner : IDisposable {
    /// <summary>
    /// The file the PEM-encoded private key used to live in, under the removed <c>Auth:KeyPath</c>. Kept
    /// only so <see cref="FileStateImport"/> can find it once on an upgraded installation.
    /// </summary>
    public const string KeyFileName = "jwt-es256.key";

    /// <summary>The <see cref="KeyProtector"/> purpose the signing key is encrypted under.</summary>
    internal const string KeyPurpose = "identity-assertion";

    /// <summary>Issuer used when <c>Auth:Host</c> is not configured.</summary>
    public const string DefaultIssuer = "watchtower";

    /// <summary>How long a minted assertion is valid.</summary>
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly KeyProtector _protector;
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
        IServiceScopeFactory scopeFactory,
        KeyProtector protector,
        IOptionsMonitor<WatchtowerOptions> options,
        TimeProvider time,
        ILogger<AuthTokenSigner> logger) {
        _scopeFactory = scopeFactory;
        _protector = protector;
        _options = options;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Loads the signing key from the database, creating it on first ever start. Called from the host's
    /// database initialization, before anything can be served.
    /// </summary>
    /// <remarks>
    /// Eager rather than on first use, and that is the multi-instance requirement rather than a
    /// preference: every instance has to agree on the key before any of them mints an assertion, or the
    /// first request after a deployment could be answered with a token whose <c>kid</c> is not in the
    /// JWKS the app already cached.
    /// </remarks>
    public async Task InitializeAsync(CancellationToken ct = default) {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _material) is not null) return;
        var material = await LoadAsync(ct);
        lock (_gate) {
            // Another caller won; the loser's material is scratch.
            if (_material is not null) {
                material.Dispose();
                return;
            }
            Volatile.Write(ref _material, material);
        }
    }

    /// <summary>
    /// Reads the <c>identity-assertion</c> row, creating it if this is the first start of the first
    /// instance.
    /// </summary>
    /// <remarks>
    /// The insert is unconditional and the unique primary key is the race guard, then everyone re-reads:
    /// two instances starting together must end up with one key, not two, because a token minted under
    /// the key that lost would fail verification against the JWKS served by the one that won.
    /// </remarks>
    private async Task<SigningMaterial> LoadAsync(CancellationToken ct) {
        var stored = await ReadKeyAsync(ct);
        if (stored is null) {
            await TryCreateKeyAsync(ct);
            stored = await ReadKeyAsync(ct)
                     ?? throw new InvalidOperationException(
                         "The identity-assertion signing key was created but could not be read back.");
        }

        var key = ECDsa.Create();
        try {
            var pem = _protector.UnprotectText(stored.PrivateKey, stored.Protection, KeyPurpose);
            try {
                key.ImportFromPem(pem);
            } catch (Exception ex) when (ex is ArgumentException or CryptographicException) {
                // Fatal rather than silently replaced: overwriting it would invalidate every assertion an
                // operator's apps are pinned to, without anyone noticing.
                throw new CryptographicException(
                    "The stored identity-assertion signing key could not be read. Restore the "
                    + "key-protection secret it was written with, or delete the signing_keys row to mint "
                    + "a new key (which invalidates every assertion apps have cached a kid for).", ex);
            }
            await ReprotectAsync(stored, pem, ct);
            return SigningMaterial.From(key);
        } catch {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Encrypts a signing key that was written before a key-protection secret existed. On load rather
    /// than by a migration pass, because this is the only moment the plaintext is in hand anyway — and
    /// because an operator who adopts the secret expects the key to become encrypted without a separate
    /// step.
    /// </summary>
    private async Task ReprotectAsync(SigningKey stored, string pem, CancellationToken ct) {
        if (!_protector.IsEncrypting || stored.Protection != KeyProtector.None) return;
        try {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
            var updated = await db.SigningKeys
                // Guarded on the row still being unprotected, so two instances starting together do not
                // both rewrite it — and so this cannot overwrite a row re-encrypted under a different
                // secret in between.
                .Where(k => k.Purpose == stored.Purpose && k.Protection == KeyProtector.None)
                .ExecuteUpdateAsync(
                    s => s
                        .SetProperty(k => k.PrivateKey, _protector.ProtectText(pem, KeyPurpose))
                        .SetProperty(k => k.Protection, _protector.CurrentProtection),
                    ct);
            if (updated > 0)
                _logger.LogInformation("Encrypted the identity-token signing key at rest.");
        } catch (Exception ex) {
            // Not fatal: the key was read fine and assertions are being minted. The next start retries.
            _logger.LogWarning(ex, "Could not encrypt the identity-token signing key at rest.");
        }
    }

    private async Task<SigningKey?> ReadKeyAsync(CancellationToken ct) {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        return await db.SigningKeys.AsNoTracking()
            .FirstOrDefaultAsync(k => k.Purpose == SigningKey.IdentityAssertionPurpose, ct);
    }

    private async Task TryCreateKeyAsync(CancellationToken ct) {
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WatchtowerDbContext>();
        db.SigningKeys.Add(new SigningKey {
            Purpose = SigningKey.IdentityAssertionPurpose,
            PrivateKey = _protector.ProtectText(generated.ExportPkcs8PrivateKeyPem(), KeyPurpose),
            Protection = _protector.CurrentProtection,
            KeyId = SigningMaterial.ComputeKeyId(generated),
            CreatedAt = _time.GetUtcNow(),
        });

        try {
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("Generated a new ES256 identity-token signing key.");
        } catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }) {
            _logger.LogInformation(
                "Another instance created the identity-token signing key first; using theirs.");
        }
    }

    /// <summary>
    /// The system realm's <c>iss</c>: the configured auth host, or <see cref="DefaultIssuer"/>. Unchanged
    /// from before realms existed, deliberately — every assertion an operator's apps are already pinned to
    /// carries this value, and a realm feature that reissued them under a new name would break consumers
    /// that have nothing to do with realms.
    /// </summary>
    public string Issuer {
        get {
            var host = _options.CurrentValue.Auth.Host;
            return string.IsNullOrWhiteSpace(host) ? DefaultIssuer : host.Trim();
        }
    }

    /// <summary>
    /// The <c>iss</c> assertions minted for <paramref name="realm"/> carry: the instance-wide
    /// <see cref="Issuer"/> for the system realm, and the realm's own login host for any other — the host
    /// <em>is</em> the realm's identity to a consumer, the same way the configured one is Watchtower's.
    /// </summary>
    /// <remarks>
    /// A realm with no login host yet cannot be reached at all (its protected routes fail closed at
    /// challenge time), so this should never be asked about one. It is answered defensively anyway, with a
    /// slug-derived value: minting an assertion with an empty <c>iss</c> would produce a token that
    /// validates against whatever a consumer happens to expect, which is the one outcome worth ruling out
    /// unconditionally.
    /// </remarks>
    public string IssuerFor(RealmIdentity realm) {
        if (realm.IsSystem) return Issuer;
        var host = realm.LoginHost?.Trim();
        if (!string.IsNullOrEmpty(host)) return host;
        var slug = realm.Slug?.Trim();
        return string.IsNullOrEmpty(slug) ? DefaultIssuer : $"{DefaultIssuer}:{slug}";
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
    /// becomes the <c>aud</c> claim, and to <paramref name="realm"/>, which decides <c>iss</c> and is
    /// stated outright as the <c>realm</c> claim.
    /// </summary>
    /// <param name="realm">
    /// The population the assertion is about — the account's realm, which is also the route's (an account
    /// only ever reaches a route of its own realm; see <see cref="RouteAccessPolicy"/>). A system-realm
    /// assertion is claim-for-claim what this signer produced before realms existed, plus <c>realm</c>.
    /// </param>
    /// <param name="groups">
    /// The account's group names, which become the <c>groups</c> claim. Unlike <c>email</c> the claim is
    /// <em>always</em> present — an empty array is the positive statement "this account is in no group",
    /// which an app mapping groups to roles needs in order to tell "no memberships" apart from "this
    /// deployment does not do groups". Omitting the argument is therefore read as the empty set, which is
    /// the fail-closed direction: an app would grant too few roles, never too many.
    /// </param>
    public string Mint(User user, string routeDomain, RealmIdentity realm, IReadOnlyList<string>? groups = null) {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeDomain);

        var material = Material;
        var now = _time.GetUtcNow();
        var claims = new Dictionary<string, object>(5, StringComparer.Ordinal) {
            // Identity-forwarding claims: the stable key is `sub`; `preferred_username` is the display
            // name and may be renamed by an administrator, so apps must not key records off it.
            ["sub"] = user.Id.ToString(CultureInfo.InvariantCulture),
            ["preferred_username"] = user.UserName,
            // The trusted channel for group membership: signed, so unlike the plaintext group header it
            // does not depend on the proxy having stripped a forged copy first (design.md §2.3).
            ["groups"] = groups is null ? Array.Empty<string>() : groups.ToArray(),
            // Always present, including on system-realm assertions: `sub` is only unique per instance and
            // `preferred_username` only per realm, so an app that stores identities has to be able to say
            // which population a name came from without inferring it from `iss`.
            ["realm"] = realm.Slug ?? Realm.SystemRealmSlug,
        };
        if (!string.IsNullOrWhiteSpace(user.Email))
            claims["email"] = user.Email;

        var descriptor = new SecurityTokenDescriptor {
            Issuer = IssuerFor(realm),
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

    /// <summary>Clock skew allowed when enforcing <c>exp</c>/<c>nbf</c> — enough to cover minor drift, no more.</summary>
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Verifies an assertion this signer minted and, on success, yields its <c>sub</c> (the
    /// <see cref="User.Id"/>) and the <c>iss</c> it carried. The verification is deliberately strict — it is
    /// the gate on the UserInfo endpoint (docs/central-auth/design.md §5.3): the signature must check out
    /// against our public key with the algorithm pinned to ES256 (so <c>alg: none</c> and RSA/HMAC-confusion
    /// tokens are rejected outright), <c>exp</c> is enforced against the host clock, and <c>iss</c> must be
    /// one of <paramref name="validIssuers"/>. The audience is <em>not</em> constrained: a token's
    /// <c>aud</c> is whichever app it was minted for, and any of ours is acceptable at UserInfo, where an
    /// app presents an assertion it itself received.
    /// </summary>
    /// <remarks>
    /// Every realm is signed with the same key pair (per-realm keys are not a v1 feature), so the issuer is
    /// the <em>only</em> thing that says which population an assertion is about — which is why it comes back
    /// out. A caller that accepts several realms' issuers has to check the resolved account against the one
    /// that was actually presented, or it would have accepted "some realm minted this" as "your realm did".
    /// </remarks>
    /// <param name="token">The assertion the caller presented.</param>
    /// <param name="validIssuers">
    /// Issuers to accept. Empty accepts nothing: "no realm to bind to" is not "any realm".
    /// </param>
    /// <param name="userId">The subject when the assertion is valid; otherwise zero.</param>
    /// <param name="issuer">The accepted <c>iss</c> when the assertion is valid; otherwise empty.</param>
    /// <returns><see langword="true"/> and the subject id when valid; otherwise <see langword="false"/>.</returns>
    public bool TryValidate(
        string? token, IReadOnlyCollection<string> validIssuers, out int userId, out string issuer) =>
        TryValidateCore(token, validIssuers, validAudiences: null, out userId, out issuer);

    /// <summary>
    /// Verifies an assertion exactly as
    /// <see cref="TryValidate(string?, IReadOnlyCollection{string}, out int, out string)"/> does and
    /// additionally binds it to the caller: its <c>aud</c> must name one of
    /// <paramref name="validAudiences"/>, compared case-insensitively because host names are.
    /// </summary>
    /// <remarks>
    /// The overload the user-scoped discovery endpoints authenticate with, and the whole of their
    /// anti-enumeration property: a stack passes the domains <em>it</em> is served on, so it can only ask
    /// about a visitor who is actually standing in front of it. An assertion some other app was handed
    /// carries that app's <c>aud</c> and is refused here, however cryptographically sound it is. Those
    /// callers know exactly which realm they belong to, so they pass that one issuer and nothing else.
    /// </remarks>
    /// <param name="token">The assertion the caller presented.</param>
    /// <param name="validIssuers">Issuers to accept; empty accepts nothing.</param>
    /// <param name="validAudiences">
    /// Audiences to accept. An empty collection accepts nothing: "no domain to bind to" is not "any domain".
    /// </param>
    /// <param name="userId">The subject when the assertion is valid; otherwise zero.</param>
    /// <returns><see langword="true"/> and the subject id when valid; otherwise <see langword="false"/>.</returns>
    public bool TryValidate(
        string? token,
        IReadOnlyCollection<string> validIssuers,
        IReadOnlyCollection<string> validAudiences,
        out int userId) {
        ArgumentNullException.ThrowIfNull(validAudiences);
        userId = 0;
        if (validAudiences.Count == 0) return false;
        return TryValidateCore(
            token,
            validIssuers,
            new HashSet<string>(validAudiences, StringComparer.OrdinalIgnoreCase),
            out userId,
            out _);
    }

    /// <summary>
    /// The one verification both overloads run. <paramref name="validAudiences"/> null means the audience is
    /// not constrained (the UserInfo case); non-null means the <c>aud</c> must be one of them.
    /// </summary>
    private bool TryValidateCore(
        string? token,
        IReadOnlyCollection<string> validIssuers,
        IReadOnlySet<string>? validAudiences,
        out int userId,
        out string issuer) {
        ArgumentNullException.ThrowIfNull(validIssuers);
        userId = 0;
        issuer = string.Empty;
        if (validIssuers.Count == 0) return false;
        if (string.IsNullOrWhiteSpace(token)) return false;

        var material = Material;
        var parameters = new TokenValidationParameters {
            // Ordinal, unlike the audience: an issuer is a value this instance produced and stored, not a
            // host name a caller typed, so the comparison has nothing to be lenient about.
            ValidIssuers = validIssuers,
            ValidateIssuer = true,
            // The aud binds a token to one app. At UserInfo any of ours is fine, so it is left unconstrained;
            // the discovery endpoints pass the caller's own domains and the delegate below decides.
            ValidateAudience = validAudiences is not null,
            IssuerSigningKey = material.ValidationKey,
            ValidateIssuerSigningKey = true,
            // Pin the algorithm: the single line that closes off `alg: none` and key-confusion attacks.
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            // Enforce exp/nbf against the host clock (movable in tests) rather than the library's DateTime.UtcNow.
            LifetimeValidator = (notBefore, expires, _, _) => {
                if (expires is null) return false;
                var now = _time.GetUtcNow().UtcDateTime;
                if (expires.Value <= now - ClockSkew) return false;
                return notBefore is not { } nbf || nbf <= now + ClockSkew;
            },
        };

        if (validAudiences is not null) {
            // Not ValidAudiences: the library compares audiences ordinally, and a host name is not a
            // case-sensitive string. A delegate replaces default audience processing entirely — and, unlike
            // the flag above, is consulted whatever ValidateAudience says, so it cannot be switched off by
            // a later edit that means to relax something else.
            parameters.AudienceValidator = (audiences, _, _) =>
                audiences is not null && audiences.Any(validAudiences.Contains);
        }

        TokenValidationResult result;
        // ECDsa instance members are not thread-safe; serialise verification the same way signing is.
        lock (_gate) {
            ObjectDisposedException.ThrowIf(_disposed, this);
            result = material.Handler.ValidateTokenAsync(token, parameters).GetAwaiter().GetResult();
        }

        if (!result.IsValid) return false;

        var jwt = (JsonWebToken)result.SecurityToken;
        if (!int.TryParse(jwt.Subject, NumberStyles.Integer, CultureInfo.InvariantCulture, out userId)) return false;

        // Read off the validated token, not off the accepted set: what the caller has to key a realm check
        // on is the value the assertion actually carried.
        issuer = jwt.Issuer;
        return true;
    }

    /// <summary>
    /// The loaded key pair. <see cref="InitializeAsync"/> is what fills this, at startup; the blocking
    /// fallback below is a safety net for a host that never ran it, not a second design.
    /// </summary>
    /// <remarks>
    /// Blocking on a database read from a property is exactly as unpleasant as it looks, which is why
    /// the eager path exists — but the alternative here is throwing on the first proxied request of a
    /// misconfigured host, and a signer that quietly works is worth one synchronous read that in
    /// practice never happens.
    /// </remarks>
    private SigningMaterial Material {
        get {
            var loaded = Volatile.Read(ref _material);
            if (loaded is not null) return loaded;
            ObjectDisposedException.ThrowIf(_disposed, this);
            InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
            return Volatile.Read(ref _material)
                   ?? throw new InvalidOperationException("The identity-assertion signing key is not loaded.");
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
    private sealed class SigningMaterial(ECDsa key, ECDsa publicKey, string keyId, string jwks) : IDisposable {
        public JsonWebTokenHandler Handler { get; } = new();
        public SigningCredentials Credentials { get; } =
            new(new ECDsaSecurityKey(key) { KeyId = keyId }, SecurityAlgorithms.EcdsaSha256);

        /// <summary>
        /// The public half as its own key, used to verify assertions at the UserInfo endpoint. A separate
        /// instance from the private signing key so validation never hands a verifier the private scalar.
        /// </summary>
        public SecurityKey ValidationKey { get; } = new ECDsaSecurityKey(publicKey) { KeyId = keyId };
        public string KeyId { get; } = keyId;
        public string Jwks { get; } = jwks;

        public void Dispose() {
            key.Dispose();
            publicKey.Dispose();
        }

        /// <summary>
        /// Derives everything the signer needs from a loaded key pair. Takes ownership of
        /// <paramref name="key"/>.
        /// </summary>
        public static SigningMaterial From(ECDsa key) {
            ECDsa? publicOnly = null;
            try {
                // The JWKS must never carry `d`: JsonWebKeyConverter emits the private parameters when the
                // key it is handed has them, so the public half is re-exported into its own instance first.
                // That same public-only key is kept for signature verification (see ValidationKey).
                publicOnly = ECDsa.Create();
                publicOnly.ImportParameters(key.ExportParameters(includePrivateParameters: false));
                var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(publicOnly));
                var keyId = Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint());

                return new SigningMaterial(key, publicOnly, keyId, RenderJwks(jwk, keyId));
            } catch {
                publicOnly?.Dispose();
                throw;
            }
        }

        /// <summary>
        /// The RFC 7638 thumbprint of a key pair's public half — the <c>kid</c>. Stored on the row as
        /// well as derived here, so an operator can tell two keys apart without decrypting either.
        /// </summary>
        public static string ComputeKeyId(ECDsa key) {
            using var publicOnly = ECDsa.Create();
            publicOnly.ImportParameters(key.ExportParameters(includePrivateParameters: false));
            var jwk = JsonWebKeyConverter.ConvertFromECDsaSecurityKey(new ECDsaSecurityKey(publicOnly));
            return Base64UrlEncoder.Encode(jwk.ComputeJwkThumbprint());
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
    }
}
