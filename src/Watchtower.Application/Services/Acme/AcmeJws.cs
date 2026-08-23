using System.Security.Cryptography;
using System.Text;

namespace Watchtower.Application.Services.Acme;

/// <summary>
/// The JOSE half of RFC 8555 — flattened JWS objects over an ES256 account key (§6.2), the RFC 7638 key
/// thumbprint, the HTTP-01 key authorization (§8.1) and the External Account Binding (§7.3.4).
/// </summary>
/// <remarks>
/// Written by hand, and small enough to read in one sitting, because ACME uses a deliberately narrow
/// slice of JOSE: one algorithm, one key type, one serialization. A general JOSE library would bring an
/// algorithm-agile parser to a place where agility is the vulnerability — and every ACME-capable .NET
/// library on offer either drags in Newtonsoft or ships a hard-coded list of public suffixes.
/// <para>
/// Every string this class emits is either base64url or a JSON object it built itself out of base64url
/// and constants, so there is nothing for a serializer to escape and nothing a caller can inject. That
/// is also why the JSON is composed with interpolation rather than <c>JsonSerializer</c>: the member
/// order of <see cref="PublicJwkJson"/> is load-bearing (RFC 7638 requires lexicographic order with no
/// whitespace), and a serializer's ordering is a property of its configuration rather than of this file.
/// </para>
/// </remarks>
internal static class AcmeJws {
    /// <summary>Base64url without padding — the only encoding JOSE uses.</summary>
    public static string Base64Url(ReadOnlySpan<byte> bytes) =>
        System.Buffers.Text.Base64Url.EncodeToString(bytes);

    /// <summary>Base64url of a string's UTF-8 bytes.</summary>
    public static string Base64Url(string value) => Base64Url(Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// The public JWK of an ES256 key, in exactly the form RFC 7638 §3.2 prescribes for a thumbprint
    /// input: the required members only, ordered lexicographically (<c>crv</c>, <c>kty</c>, <c>x</c>,
    /// <c>y</c>), with no whitespace. One string therefore serves both purposes — the <c>jwk</c> member
    /// of a protected header and the thumbprint's hash input — and the two cannot drift apart.
    /// </summary>
    public static string PublicJwkJson(ECDsa key) {
        ArgumentNullException.ThrowIfNull(key);
        var parameters = key.ExportParameters(includePrivateParameters: false);
        var q = parameters.Q;
        if (q.X is null || q.Y is null)
            throw new CryptographicException("The account key has no public point to publish.");
        // The coordinates are fixed-width for P-256 (32 bytes each) and ExportParameters already pads
        // them — a shorter encoding would produce a valid-looking JWK that hashes to the wrong thumbprint.
        return $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{Base64Url(q.X)}\",\"y\":\"{Base64Url(q.Y)}\"}}";
    }

    /// <summary>The RFC 7638 thumbprint of the account key: base64url(SHA-256(<see cref="PublicJwkJson"/>)).</summary>
    public static string Thumbprint(ECDsa key) =>
        Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(PublicJwkJson(key))));

    /// <summary>
    /// The HTTP-01 key authorization (RFC 8555 §8.1): the challenge token, a dot, and the account key's
    /// thumbprint. This exact string is what <c>/.well-known/acme-challenge/{token}</c> must return.
    /// </summary>
    public static string KeyAuthorization(string token, ECDsa accountKey) {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        return token + "." + Thumbprint(accountKey);
    }

    /// <summary>
    /// Signs one ACME request as a flattened JWS (RFC 8555 §6.2).
    /// </summary>
    /// <param name="kid">
    /// The account URL. Null produces a <c>jwk</c>-carrying header, which is legal only for
    /// <c>newAccount</c> and <c>revokeCert</c>; every other request identifies the account by
    /// <c>kid</c>. The two are mutually exclusive — §6.2 says a JWS carrying both must be rejected — so
    /// this method emits exactly one of them and there is no way for a caller to ask for both.
    /// </param>
    /// <param name="payloadJson">
    /// The request body, or null for POST-as-GET (§6.3), whose payload is the <em>empty string</em> —
    /// not <c>null</c>, not <c>{}</c>, and not base64url of anything.
    /// </param>
    public static string Sign(ECDsa key, string url, string nonce, string? kid, string? payloadJson) {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(nonce);

        // Every interpolated value is escaped, the nonce included: it is an opaque string the CA chose,
        // so it is the one part of this header that is not ours. A stray quote in it would produce a
        // header that signs one thing and parses as another.
        var header = kid is null
            ? $"{{\"alg\":\"ES256\",\"nonce\":\"{JsonEscape(nonce)}\",\"url\":\"{JsonEscape(url)}\",\"jwk\":{PublicJwkJson(key)}}}"
            : $"{{\"alg\":\"ES256\",\"nonce\":\"{JsonEscape(nonce)}\",\"url\":\"{JsonEscape(url)}\",\"kid\":\"{JsonEscape(kid)}\"}}";

        var protectedHeader = Base64Url(header);
        // POST-as-GET: the payload member is present and empty, which is what distinguishes it from a
        // request with an empty JSON object body.
        var payload = payloadJson is null ? "" : Base64Url(payloadJson);
        var signature = Base64Url(SignInput(key, protectedHeader, payload));

        return $"{{\"protected\":\"{protectedHeader}\",\"payload\":\"{payload}\",\"signature\":\"{signature}\"}}";
    }

    /// <summary>
    /// The External Account Binding (RFC 8555 §7.3.4): an inner JWS, signed with the HMAC key the CA
    /// issued out of band, whose payload is the account key's public JWK. It proves to the CA that the
    /// party registering this key is the customer the key id belongs to.
    /// </summary>
    /// <param name="eabHmacKeyBase64Url">
    /// The MAC key exactly as CAs hand it out: base64url, usually unpadded. Standard base64 is accepted
    /// too, because operators paste what their CA's dashboard shows and several show the padded form.
    /// </param>
    public static string ExternalAccountBinding(
        ECDsa accountKey, string newAccountUrl, string eabKeyId, string eabHmacKeyBase64Url) {
        ArgumentNullException.ThrowIfNull(accountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(newAccountUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(eabKeyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eabHmacKeyBase64Url);

        // HS256 rather than ES256, and no nonce: the inner JWS is not an ACME request, it is a payload
        // of one, and §7.3.4 spells out that its protected header carries alg/kid/url and nothing else.
        var header = $"{{\"alg\":\"HS256\",\"kid\":\"{JsonEscape(eabKeyId)}\",\"url\":\"{JsonEscape(newAccountUrl)}\"}}";
        var protectedHeader = Base64Url(header);
        var payload = Base64Url(PublicJwkJson(accountKey));

        var macKey = DecodeMacKey(eabHmacKeyBase64Url);
        try {
            var signature = Base64Url(
                HMACSHA256.HashData(macKey, Encoding.ASCII.GetBytes($"{protectedHeader}.{payload}")));
            return $"{{\"protected\":\"{protectedHeader}\",\"payload\":\"{payload}\",\"signature\":\"{signature}\"}}";
        } finally {
            CryptographicOperations.ZeroMemory(macKey);
        }
    }

    /// <summary>
    /// The raw ES256 signature over <c>protected + "." + payload</c>.
    /// </summary>
    /// <remarks>
    /// ASCII, not UTF-8: both halves are base64url by construction, so the two encodings agree — and
    /// spelling it out is what documents that the signing input is the concatenation of the encoded
    /// parts rather than of the decoded ones.
    /// <para>
    /// The signature format is the one thing about ES256 that a .NET implementation gets wrong by
    /// default: <c>SignData</c> without a format argument produces DER, and JOSE requires the fixed-width
    /// r‖s concatenation (64 bytes for P-256). A DER signature is a well-formed byte string that every
    /// CA rejects.
    /// </para>
    /// </remarks>
    private static byte[] SignInput(ECDsa key, string protectedHeader, string payload) =>
        key.SignData(
            Encoding.ASCII.GetBytes($"{protectedHeader}.{payload}"),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>
    /// Decodes an EAB MAC key. Base64url first (what the RFC and most CAs use), then standard base64 —
    /// the two differ only in two alphabet characters, and an operator pasting from a dashboard has no
    /// way to know which they were given.
    /// </summary>
    private static byte[] DecodeMacKey(string value) {
        var trimmed = value.Trim();
        if (System.Buffers.Text.Base64Url.IsValid(trimmed))
            return System.Buffers.Text.Base64Url.DecodeFromChars(trimmed);
        if (Convert.TryFromBase64String(trimmed, new byte[trimmed.Length], out _))
            return Convert.FromBase64String(trimmed);
        throw new FormatException("The External Account Binding HMAC key is not valid base64url.");
    }

    /// <summary>
    /// Escapes a value for one of the JSON string literals above. URLs and account ids contain nothing
    /// that needs escaping in practice, but "in practice" is not a property a signature input should
    /// rest on — a stray quote would otherwise produce a header that signs one thing and reads as
    /// another.
    /// </summary>
    private static string JsonEscape(string value) {
        if (value.AsSpan().IndexOfAny('"', '\\') < 0 && !value.Any(char.IsControl)) return value;
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
            switch (c) {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                default:
                    if (char.IsControl(c)) builder.Append($"\\u{(int)c:x4}");
                    else builder.Append(c);
                    break;
            }
        return builder.ToString();
    }
}
