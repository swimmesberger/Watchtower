using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="AuthTokenSigner"/> — the ES256 assertion an app receives as <c>X-Watchtower-Jwt</c>
/// and the JWKS it verifies that assertion against (docs/central-auth/design.md §2.3, §4).
/// </summary>
/// <remarks>
/// Validation goes through <see cref="JsonWebTokenHandler"/> configured the way a consuming application
/// would configure it — keys taken from the published JWKS, algorithm pinned, audience and issuer checked.
/// Asserting on the decoded payload alone would prove the claims are present but not that anyone else can
/// verify them, which is the only property that matters here.
/// </remarks>
public sealed class AuthTokenSignerTests {
    private const string AppDomain = "app.example.invalid";

    [Fact]
    public void MintedToken_VerifiesAgainstThePublishedJwks() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var token = signer.Mint(User("alice", "alice@example.invalid"), AppDomain);
        var result = Validate(token, signer, AppDomain);

        Assert.True(result.IsValid, result.Exception?.ToString());
        var jwt = Assert.IsType<JsonWebToken>(result.SecurityToken);

        Assert.Equal(AuthTokenSigner.DefaultIssuer, jwt.Issuer);
        Assert.Equal(AppDomain, Assert.Single(jwt.Audiences));
        Assert.Equal("7", jwt.Subject);
        Assert.Equal("alice", jwt.GetClaim("preferred_username").Value);
        Assert.Equal("alice@example.invalid", jwt.GetClaim("email").Value);
        // ES256 specifically: a token this validated under a different algorithm would be a different
        // security property (`alg: none` and HMAC confusion are the classic ones).
        Assert.Equal(SecurityAlgorithms.EcdsaSha256, jwt.Alg);
        // The header names the key, so an app that rotates its cached JWKS knows which entry to use.
        Assert.Equal(signer.KeyId, jwt.Kid);
        Assert.Equal(host.Time.Now.AddMinutes(5).UtcDateTime, jwt.ValidTo, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TokenMintedForOneApp_IsRejectedByAnother() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var token = signer.Mint(User("alice"), AppDomain);

        // The whole point of `aud`: a compromised or merely curious upstream cannot replay the assertion
        // it was handed against a different app (design.md §2.3).
        Assert.False(Validate(token, signer, "other.example.invalid").IsValid);
    }

    [Fact]
    public void ExpiredToken_IsRejected() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var token = signer.Mint(User("alice"), AppDomain);
        host.Time.Advance(TimeSpan.FromMinutes(30));

        Assert.True(Validate(token, signer, AppDomain).IsValid);
        // Half an hour on, the five-minute window the mint stamped in has long closed — which is what makes
        // the assertion a statement about one request rather than a credential the app can hold on to.
        Assert.False(Validate(token, signer, AppDomain, now: host.Time.Now).IsValid);
    }

    [Fact]
    public void AccountWithoutAnEmail_OmitsTheClaim() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var jwt = new JsonWebToken(signer.Mint(User("alice", email: null), AppDomain));

        Assert.False(jwt.TryGetClaim("email", out _));
        Assert.Equal("alice", jwt.GetClaim("preferred_username").Value);
    }

    [Fact]
    public void Issuer_IsTheAuthHostWhenOneIsConfigured() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", "watchtower.example.invalid"));
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var result = Validate(signer.Mint(User("alice"), AppDomain), signer, AppDomain,
            issuer: "watchtower.example.invalid");

        Assert.True(result.IsValid, result.Exception?.ToString());
    }

    [Fact]
    public void Jwks_PublishesThePublicKeyOnly() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        using var document = JsonDocument.Parse(signer.JwksDocument);
        var key = Assert.Single(document.RootElement.GetProperty("keys").EnumerateArray());

        Assert.Equal("EC", key.GetProperty("kty").GetString());
        Assert.Equal("P-256", key.GetProperty("crv").GetString());
        Assert.Equal("ES256", key.GetProperty("alg").GetString());
        Assert.Equal("sig", key.GetProperty("use").GetString());
        Assert.Equal(signer.KeyId, key.GetProperty("kid").GetString());
        // The private scalar must never leave the process. Publishing it would hand every app the ability
        // to mint assertions for every other app.
        Assert.False(key.TryGetProperty("d", out _));
    }

    [Fact]
    public void KeyIsPersisted_SoRestartsKeepTheSameKid_AndOldTokensStayValid() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();
        var token = signer.Mint(User("alice"), AppDomain);
        var keyId = signer.KeyId;

        // A restart against the same data directory: without persistence every restart would rotate the
        // key, invalidating in-flight assertions and forcing every app to refetch the JWKS.
        using var restarted = host.Restart();
        var reloaded = restarted.Services.GetRequiredService<AuthTokenSigner>();

        Assert.Equal(keyId, reloaded.KeyId);
        Assert.Equal(signer.JwksDocument, reloaded.JwksDocument);
        Assert.True(Validate(token, reloaded, AppDomain).IsValid);
    }

    [Fact]
    public void KeyId_IsTheRfc7638ThumbprintOfThePublishedKey() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        // Recomputed from the published document rather than trusted from the signer: an app that pins a
        // key by thumbprint derives it exactly this way, and a mismatch would break that pin silently.
        var published = JsonWebKeySet.Create(signer.JwksDocument).Keys.Single();
        Assert.Equal(signer.KeyId, Base64UrlEncoder.Encode(published.ComputeJwkThumbprint()));
    }

    // ── TryValidate: the gate on the UserInfo endpoint (design.md §5.3) ─────────

    [Fact]
    public void TryValidate_AcceptsAFreshTokenAndYieldsTheSubject() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var token = signer.Mint(User("alice"), AppDomain);

        Assert.True(signer.TryValidate(token, out var userId));
        Assert.Equal(7, userId);
    }

    [Fact]
    public void TryValidate_RejectsAnExpiredToken() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var token = signer.Mint(User("alice"), AppDomain);
        Assert.True(signer.TryValidate(token, out _));

        // Past the five-minute window (and past the 30 s skew): an assertion about one request is not a
        // credential the caller can hold on to and present later.
        host.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.False(signer.TryValidate(token, out _));
    }

    [Fact]
    public void TryValidate_RejectsAWrongIssuer() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", "issuer-a.example.invalid"));
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();
        var token = signer.Mint(User("alice"), AppDomain);

        // Same signing key (the restart shares the data directory), but the issuer we now vouch for has
        // changed — a token stamped by the old issuer must not validate.
        using var restarted = host.Restart(("Watchtower:Auth:Host", "issuer-b.example.invalid"));
        var reissued = restarted.Services.GetRequiredService<AuthTokenSigner>();

        Assert.False(reissued.TryValidate(token, out _));
    }

    [Fact]
    public void TryValidate_RejectsAnUnsignedNoneToken() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var exp = host.Time.Now.AddMinutes(5).ToUnixTimeSeconds();
        var header = Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        var payload = Base64UrlEncoder.Encode(
            $$"""{"iss":"{{AuthTokenSigner.DefaultIssuer}}","sub":"7","exp":{{exp}}}""");

        // The classic downgrade: an attacker strips the signature and sets alg to none. Pinning ES256 (and
        // requiring a signature) rejects it outright.
        Assert.False(signer.TryValidate($"{header}.{payload}.", out _));
    }

    [Fact]
    public void TryValidate_RejectsAnAlgorithmConfusionToken() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        // A well-formed HS256 token: the algorithm pin means a symmetric-signed token is never even a
        // candidate, whatever key it claims.
        using var secret = new HMACSHA256(RandomNumberGenerator.GetBytes(32));
        var hs256 = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor {
            Issuer = AuthTokenSigner.DefaultIssuer,
            Claims = new Dictionary<string, object> { ["sub"] = "7" },
            Expires = host.Time.Now.AddMinutes(5).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(secret.Key), SecurityAlgorithms.HmacSha256),
        });

        Assert.False(signer.TryValidate(hs256, out _));
    }

    [Fact]
    public void TryValidate_RejectsATamperedSignature() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var token = signer.Mint(User("alice"), AppDomain);
        // Flip the last character of the signature segment; the ES256 verification then fails.
        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        Assert.False(signer.TryValidate(tampered, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b.c")]
    public void TryValidate_RejectsMalformedInput(string token) {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        Assert.False(signer.TryValidate(token, out var userId));
        Assert.Equal(0, userId);
    }

    // ── TryValidate with audiences: the gate on the tenant-discovery endpoints ──
    //
    // Same verification as above plus the binding that makes those endpoints safe to expose: the caller
    // passes the domains it is itself served on, so it can only ever present an assertion minted for one of
    // them — i.e. one it was handed by a visitor who is actually there.

    [Fact]
    public void TryValidateWithAudiences_AcceptsATokenMintedForOneOfTheCallersDomains() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var token = signer.Mint(User("alice"), AppDomain);

        // A stack serving several domains (a managed subdomain plus a customer's own, say) accepts an
        // assertion minted for any of them — they are all "visiting this stack".
        Assert.True(signer.TryValidate(token, ["first.example.invalid", AppDomain], out var userId));
        Assert.Equal(7, userId);
    }

    [Fact]
    public void TryValidateWithAudiences_RejectsATokenMintedForAnotherApp() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var token = signer.Mint(User("alice"), "other.example.invalid");

        // The anti-enumeration property: an assertion a different app received cannot be replayed here to
        // ask what its bearer may reach.
        Assert.False(signer.TryValidate(token, [AppDomain], out var userId));
        Assert.Equal(0, userId);
    }

    [Fact]
    public void TryValidateWithAudiences_MatchesHostNamesCaseInsensitively() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var token = signer.Mint(User("alice"), "App.Example.INVALID");

        // A host name is not a case-sensitive string, and the route row's casing is an operator's typing —
        // it must not decide whether a visitor's own assertion is accepted.
        Assert.True(signer.TryValidate(token, [AppDomain], out _));
    }

    [Fact]
    public void TryValidateWithAudiences_RejectsWhenTheCallerHasNoDomains() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var token = signer.Mint(User("alice"), AppDomain);

        // "Nothing to bind to" is not "bind to anything": a stack Watchtower serves no domain for could not
        // have been forwarded an assertion in the first place.
        Assert.False(signer.TryValidate(token, [], out _));
    }

    [Fact]
    public void TryValidateWithAudiences_StillEnforcesEveryOtherCheck() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();
        var token = signer.Mint(User("alice"), AppDomain);
        Assert.True(signer.TryValidate(token, [AppDomain], out _));

        // Expiry: the audience being right does not make a stale assertion current.
        host.Time.Advance(TimeSpan.FromMinutes(30));
        Assert.False(signer.TryValidate(token, [AppDomain], out _));

        // Tampering, on a token that is otherwise within its window.
        var fresh = signer.Mint(User("alice"), AppDomain);
        Assert.False(signer.TryValidate(fresh[..^1] + (fresh[^1] == 'A' ? 'B' : 'A'), [AppDomain], out _));

        // And the algorithm pin — this one carries the *correct* audience, so its rejection can only be the
        // pin doing its job rather than the audience check masking it.
        using var secret = new HMACSHA256(RandomNumberGenerator.GetBytes(32));
        var hs256 = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor {
            Issuer = AuthTokenSigner.DefaultIssuer,
            Audience = AppDomain,
            Claims = new Dictionary<string, object> { ["sub"] = "7" },
            Expires = host.Time.Now.AddMinutes(5).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(secret.Key), SecurityAlgorithms.HmacSha256),
        });
        Assert.False(signer.TryValidate(hs256, [AppDomain], out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-jwt")]
    public void TryValidateWithAudiences_RejectsMalformedInput(string token) {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        Assert.False(signer.TryValidate(token, [AppDomain], out var userId));
        Assert.Equal(0, userId);
    }

    /// <summary>
    /// Validates the way a protected application would: keys from the published JWKS, pinned algorithm,
    /// checked issuer and audience. <paramref name="now"/> stands in for the verifier's clock, since the
    /// signer stamps its timestamps from the host's (movable) one.
    /// </summary>
    private static TokenValidationResult Validate(
        string token, AuthTokenSigner signer, string audience, string? issuer = null, DateTimeOffset? now = null) =>
        new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters {
            ValidIssuer = issuer ?? AuthTokenSigner.DefaultIssuer,
            ValidAudience = audience,
            IssuerSigningKeys = JsonWebKeySet.Create(signer.JwksDocument).GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ClockSkew = TimeSpan.Zero,
            LifetimeValidator = (notBefore, expires, _, _) => {
                var instant = (now ?? DateTimeOffset.UtcNow).UtcDateTime;
                return notBefore <= instant && expires > instant;
            },
        }).GetAwaiter().GetResult();

    private static User User(string userName, string? email = null) => new() {
        Id = 7,
        UserName = userName,
        NormalizedUserName = userName.ToUpperInvariant(),
        Email = email,
        PasswordHash = string.Empty,
        SecurityStamp = string.Empty,
        ConcurrencyStamp = string.Empty,
    };
}
