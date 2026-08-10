using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The realm-shaped half of <see cref="AuthTokenSigner"/> (docs/central-auth/design.md §13): where a
/// realm's <c>iss</c> comes from, the <c>realm</c> claim, and — the compatibility promise — that an
/// operator-realm assertion is otherwise exactly what this signer produced before realms existed.
/// </summary>
public sealed class RealmTokenTests {
    private const string AppDomain = "app.example.invalid";
    private const string AuthHost = "watchtower.example.invalid";

    [Fact]
    public void SystemRealm_KeepsTodaysIssuer() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", AuthHost));
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var jwt = new JsonWebToken(signer.Mint(User(), AppDomain, RealmIdentity.System));

        // Apps are pinned to this value; a realm feature that changed it would break consumers that have
        // nothing to do with realms.
        Assert.Equal(AuthHost, jwt.Issuer);
        Assert.Equal(signer.Issuer, jwt.Issuer);
    }

    [Fact]
    public void ARealm_IssuesUnderItsOwnLoginHost() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", AuthHost));
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var jwt = new JsonWebToken(signer.Mint(
            User(), AppDomain, new RealmIdentity("acme", "login.acme.invalid", IsSystem: false)));

        Assert.Equal("login.acme.invalid", jwt.Issuer);
        Assert.Equal("acme", jwt.GetClaim("realm").Value);
    }

    /// <summary>
    /// A realm with no login host cannot be reached at all (its protected routes fail closed at challenge
    /// time), so this should never happen — but an empty <c>iss</c> would validate against whatever a
    /// consumer happened to expect, so it is ruled out unconditionally rather than left to that argument.
    /// </summary>
    [Fact]
    public void ARealmWithoutALoginHost_StillGetsANonEmptyIssuer() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var issuer = signer.IssuerFor(new RealmIdentity("acme", AuthHost: null, IsSystem: false));

        Assert.False(string.IsNullOrWhiteSpace(issuer));
        Assert.NotEqual(signer.Issuer, issuer);
        Assert.Contains("acme", issuer, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRealmClaim_IsAlwaysPresent_IncludingForTheOperatorRealm() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var jwt = new JsonWebToken(signer.Mint(User(), AppDomain, RealmIdentity.System));

        // `sub` is unique per instance and `preferred_username` only per realm, so an app that stores
        // identities has to be told the population outright rather than inferring it from `iss`.
        Assert.Equal(Realm.SystemRealmSlug, jwt.GetClaim("realm").Value);
    }

    /// <summary>
    /// The compatibility promise, stated as a set: an operator-realm assertion carries exactly the claims
    /// it always did, plus <c>realm</c> and nothing else.
    /// </summary>
    [Fact]
    public void OperatorRealmAssertions_CarryTodaysClaimsPlusRealm() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", AuthHost));
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var jwt = new JsonWebToken(
            signer.Mint(User("alice@example.invalid"), AppDomain, RealmIdentity.System, ["staff"]));

        Assert.Equal(
            ["aud", "email", "exp", "groups", "iat", "iss", "nbf", "preferred_username", "realm", "sub"],
            jwt.Claims.Select(c => c.Type).Distinct().Order());
        Assert.Equal("7", jwt.Subject);
        Assert.Equal("alice", jwt.GetClaim("preferred_username").Value);
        Assert.Equal("alice@example.invalid", jwt.GetClaim("email").Value);
        Assert.Equal(["staff"], jwt.GetPayloadValue<string[]>("groups"));
        Assert.Equal(AppDomain, Assert.Single(jwt.Audiences));
    }

    [Fact]
    public void ValidationAcceptsOnlyTheIssuersItWasGiven() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", AuthHost));
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();
        var acme = new RealmIdentity("acme", "login.acme.invalid", IsSystem: false);

        var operatorToken = signer.Mint(User(), AppDomain, RealmIdentity.System);
        var acmeToken = signer.Mint(User(), AppDomain, acme);
        var acmeIssuer = signer.IssuerFor(acme);

        // Same key pair signs both — the issuer is the only thing separating them, which is exactly why it
        // is enforced rather than merely reported.
        Assert.True(signer.TryValidate(acmeToken, [acmeIssuer], out var userId, out var issuer));
        Assert.Equal(7, userId);
        Assert.Equal(acmeIssuer, issuer);
        Assert.False(signer.TryValidate(operatorToken, [acmeIssuer], out _, out _));

        // A surface with no realm in context accepts the whole set and is told which one answered.
        Assert.True(signer.TryValidate(operatorToken, [signer.Issuer, acmeIssuer], out _, out var which));
        Assert.Equal(signer.Issuer, which);
    }

    [Fact]
    public void AnEmptyIssuerSet_AcceptsNothing() {
        using var host = AuthTestHost.Start();
        var signer = host.Services.GetRequiredService<AuthTokenSigner>();

        var token = signer.Mint(User(), AppDomain, RealmIdentity.System);

        // "No realm to bind to" is not "any realm", the same reading the empty audience set already had.
        Assert.False(signer.TryValidate(token, [], out _, out _));
        Assert.False(signer.TryValidate(token, [], [AppDomain], out _));
    }

    [Fact]
    public async Task KnownIssuers_CoverEveryRealm() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", AuthHost));
        var acme = await host.AddRealmAsync("acme", "login.acme.invalid");
        var pending = await host.AddRealmAsync("pending");

        await using var scope = host.Services.CreateAsyncScope();
        var realms = scope.ServiceProvider.GetRequiredService<RealmResolver>();
        var signer = scope.ServiceProvider.GetRequiredService<AuthTokenSigner>();

        var issuers = await realms.IssuersAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Realm.SystemRealmId, issuers[signer.Issuer]);
        Assert.Equal(acme, issuers["login.acme.invalid"]);
        // Even the unreachable realm has an entry, so a token can never match an issuer nobody claims.
        Assert.Equal(pending, issuers[signer.IssuerFor(new RealmIdentity("pending", null, false))]);
    }

    /// <summary>
    /// <c>Auth:Host</c> is configuration and can be pointed at a host a realm already holds — after the
    /// handlers, which refuse the reverse order, have had their say. The map still has to be built (the
    /// auth path cannot start throwing because of a configuration edit), the first realm wins, and the
    /// loser's assertions are then refused by the caller's own realm check rather than silently attributed
    /// to the winner. A warning is logged so the symptom is traceable to its cause.
    /// </summary>
    [Fact]
    public async Task ARealmCollidingWithTheConfiguredIssuer_StillYieldsAUsableMap() {
        using var host = AuthTestHost.Start(("Watchtower:Auth:Host", AuthHost));
        // Written directly: realms.create refuses this, which is why it can only arise the other way round.
        var shadowing = await host.AddRealmAsync("shadow", AuthHost);

        await using var scope = host.Services.CreateAsyncScope();
        var realms = scope.ServiceProvider.GetRequiredService<RealmResolver>();
        var signer = scope.ServiceProvider.GetRequiredService<AuthTokenSigner>();

        var issuers = await realms.IssuersAsync(TestContext.Current.CancellationToken);

        // One entry for the shared issuer, and it is the operator realm's — ordered by slug, "operator"
        // comes before "shadow", and either way the loser's tokens resolve to a realm that is not theirs
        // and are refused.
        Assert.Equal(Realm.SystemRealmId, issuers[signer.Issuer]);
        Assert.NotEqual(shadowing, issuers[signer.Issuer]);
    }

    private static User User(string? email = null) => new() {
        Id = 7,
        UserName = "alice",
        NormalizedUserName = "ALICE",
        Email = email,
        PasswordHash = string.Empty,
        SecurityStamp = string.Empty,
        ConcurrencyStamp = string.Empty,
    };
}
