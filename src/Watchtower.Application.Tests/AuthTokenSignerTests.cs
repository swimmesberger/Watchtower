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
