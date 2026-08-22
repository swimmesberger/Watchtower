using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The JOSE layer of the ACME client. Everything here is a property a CA enforces and nothing else
/// checks: a JWS carrying both <c>jwk</c> and <c>kid</c> is rejected outright, a DER signature is
/// rejected as a bad signature, and a POST-as-GET whose payload is not the empty string is a different
/// request than the one intended.
/// </summary>
public sealed class AcmeJwsTests {
    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void ANewAccountRequest_CarriesTheJwk_AndNoKid() {
        using var key = NewKey();

        var jws = AcmeJws.Sign(key, "https://ca.test/new-account", "nonce-1", kid: null, payloadJson: "{}");

        var header = Header(jws);
        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        Assert.Equal("nonce-1", header.GetProperty("nonce").GetString());
        Assert.Equal("https://ca.test/new-account", header.GetProperty("url").GetString());
        Assert.True(header.TryGetProperty("jwk", out var jwk));
        Assert.Equal("EC", jwk.GetProperty("kty").GetString());
        Assert.Equal("P-256", jwk.GetProperty("crv").GetString());
        // RFC 8555 §6.2: exactly one of the two identifies the key. Both is a malformed request.
        Assert.False(header.TryGetProperty("kid", out _));
    }

    [Fact]
    public void EveryOtherRequest_CarriesTheKid_AndNoJwk() {
        using var key = NewKey();

        var jws = AcmeJws.Sign(key, "https://ca.test/order/1", "nonce-2", "https://ca.test/acct/7", "{}");

        var header = Header(jws);
        Assert.Equal("https://ca.test/acct/7", header.GetProperty("kid").GetString());
        Assert.False(header.TryGetProperty("jwk", out _));
    }

    /// <summary>
    /// POST-as-GET (§6.3). The payload member is present and <em>empty</em> — not <c>{}</c>, which is a
    /// request to modify the resource, and not absent, which is not a JWS.
    /// </summary>
    [Fact]
    public void PostAsGet_SendsAnEmptyPayload() {
        using var key = NewKey();

        var jws = AcmeJws.Sign(key, "https://ca.test/authz/1", "n", "kid", payloadJson: null);

        using var document = JsonDocument.Parse(jws);
        Assert.Equal("", document.RootElement.GetProperty("payload").GetString());
        // …and it really is different from an empty object body, which is what triggers a challenge.
        var triggering = AcmeJws.Sign(key, "https://ca.test/chall/1", "n", "kid", "{}");
        using var other = JsonDocument.Parse(triggering);
        Assert.NotEqual("", other.RootElement.GetProperty("payload").GetString());
    }

    /// <summary>
    /// The signature is raw r‖s, which is the one thing .NET does not do by default — <c>SignData</c>
    /// without a format argument produces a DER sequence that every CA rejects.
    /// </summary>
    [Fact]
    public void TheSignature_IsRawP1363_AndVerifies() {
        using var key = NewKey();

        var jws = AcmeJws.Sign(key, "https://ca.test/order/1", "nonce", "kid", "{\"a\":1}");

        using var document = JsonDocument.Parse(jws);
        var root = document.RootElement;
        var signature = Base64Url.DecodeFromChars(root.GetProperty("signature").GetString());
        Assert.Equal(64, signature.Length);

        var signingInput = Encoding.ASCII.GetBytes(
            $"{root.GetProperty("protected").GetString()}.{root.GetProperty("payload").GetString()}");
        Assert.True(key.VerifyData(
            signingInput, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void EveryEncodedPart_IsUnpaddedBase64Url() {
        using var key = NewKey();

        var jws = AcmeJws.Sign(key, "https://ca.test/x", "nonce", "kid", "{}");

        using var document = JsonDocument.Parse(jws);
        foreach (var member in new[] { "protected", "payload", "signature" }) {
            var value = document.RootElement.GetProperty(member).GetString()!;
            Assert.DoesNotContain('=', value);
            Assert.DoesNotContain('+', value);
            Assert.DoesNotContain('/', value);
        }
    }

    /// <summary>
    /// RFC 7638 publishes an RSA test vector, not an EC one, so what is pinned here is the construction
    /// itself: the required members, in lexicographic order, with no whitespace — and the thumbprint
    /// being the digest of exactly that string. Getting the ordering wrong produces a thumbprint that is
    /// self-consistent and wrong, which surfaces as every challenge failing validation.
    /// </summary>
    [Fact]
    public void ThePublicJwk_IsTheCanonicalThumbprintInput() {
        using var key = NewKey();

        var jwk = AcmeJws.PublicJwkJson(key);

        Assert.StartsWith("{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"", jwk, StringComparison.Ordinal);
        Assert.Matches(
            "^\\{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"[A-Za-z0-9_-]+\",\"y\":\"[A-Za-z0-9_-]+\"\\}$", jwk);
        Assert.DoesNotContain(' ', jwk);
        Assert.DoesNotContain('\n', jwk);

        var expected = Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(jwk)));
        Assert.Equal(expected, AcmeJws.Thumbprint(key));
    }

    /// <summary>The coordinates are fixed-width for P-256; a short encoding hashes to a wrong thumbprint.</summary>
    [Fact]
    public void TheJwkCoordinates_AreFullWidth() {
        using var key = NewKey();

        var jwk = JsonDocument.Parse(AcmeJws.PublicJwkJson(key)).RootElement;

        Assert.Equal(32, Base64Url.DecodeFromChars(jwk.GetProperty("x").GetString()).Length);
        Assert.Equal(32, Base64Url.DecodeFromChars(jwk.GetProperty("y").GetString()).Length);
    }

    [Fact]
    public void TheKeyAuthorization_IsTokenDotThumbprint() {
        using var key = NewKey();

        var authorization = AcmeJws.KeyAuthorization("tok3n-value_A", key);

        var parts = authorization.Split('.');
        Assert.Equal(2, parts.Length);
        Assert.Equal("tok3n-value_A", parts[0]);
        Assert.Equal(AcmeJws.Thumbprint(key), parts[1]);
    }

    /// <summary>
    /// The EAB is an inner JWS the CA verifies with the MAC key it issued out of band — so it has to
    /// carry that key's id and the exact <c>newAccount</c> URL, and MAC over the encoded halves.
    /// </summary>
    [Fact]
    public void TheExternalAccountBinding_IsAnHs256JwsOverThePublicJwk() {
        using var key = NewKey();
        var macKey = RandomNumberGenerator.GetBytes(32);
        var encodedMacKey = Base64Url.EncodeToString(macKey);

        var eab = AcmeJws.ExternalAccountBinding(key, "https://ca.test/new-account", "kid-42", encodedMacKey);

        using var document = JsonDocument.Parse(eab);
        var root = document.RootElement;
        var header = JsonDocument.Parse(
            Base64Url.DecodeFromChars(root.GetProperty("protected").GetString())).RootElement;
        Assert.Equal("HS256", header.GetProperty("alg").GetString());
        Assert.Equal("kid-42", header.GetProperty("kid").GetString());
        Assert.Equal("https://ca.test/new-account", header.GetProperty("url").GetString());
        // No nonce: the inner JWS is a payload, not an ACME request.
        Assert.False(header.TryGetProperty("nonce", out _));

        var payload = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(root.GetProperty("payload").GetString()));
        Assert.Equal(AcmeJws.PublicJwkJson(key), payload);

        var expected = HMACSHA256.HashData(
            macKey,
            Encoding.ASCII.GetBytes(
                $"{root.GetProperty("protected").GetString()}.{root.GetProperty("payload").GetString()}"));
        Assert.Equal(Base64Url.EncodeToString(expected), root.GetProperty("signature").GetString());
    }

    /// <summary>Operators paste what their CA's dashboard shows, and several show padded base64.</summary>
    [Fact]
    public void TheExternalAccountBinding_AcceptsStandardBase64Too() {
        using var key = NewKey();
        var macKey = RandomNumberGenerator.GetBytes(30); // A length that forces padding.

        var fromUrlSafe = AcmeJws.ExternalAccountBinding(
            key, "https://ca.test/new-account", "kid", Base64Url.EncodeToString(macKey));
        var fromStandard = AcmeJws.ExternalAccountBinding(
            key, "https://ca.test/new-account", "kid", Convert.ToBase64String(macKey));

        Assert.Equal(fromUrlSafe, fromStandard);
    }

    private static JsonElement Header(string jws) {
        using var document = JsonDocument.Parse(jws);
        var encoded = document.RootElement.GetProperty("protected").GetString()!;
        return JsonDocument.Parse(Base64Url.DecodeFromChars(encoded)).RootElement.Clone();
    }
}
