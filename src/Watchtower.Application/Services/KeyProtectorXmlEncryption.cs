using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;

namespace Watchtower.Application.Services;

/// <summary>
/// Encrypts the ASP.NET data-protection key ring at rest with the same secret as every other private
/// key Watchtower stores (ADR-0024). Without this the ring would be the one thing in
/// <c>data_protection_keys</c> a database dump hands over in the clear — and it is the key material
/// behind session-adjacent payloads, so it is not the one to leave out.
/// </summary>
/// <remarks>
/// <para>
/// ASP.NET's own at-rest options are a Windows DPAPI blob or an X.509 certificate. Neither fits: the
/// shipped image is Linux, and a certificate would be a second secret to mount, rotate and lose. This
/// is the third option the framework leaves open — an <see cref="IXmlEncryptor"/> of one's own — and it
/// reuses the key protection that already exists rather than inventing a parallel one.
/// </para>
/// <para>
/// <b>The type name is part of the data format.</b> The key manager records
/// <see cref="KeyProtectorXmlDecryptor"/>'s assembly-qualified name on the encrypted element and
/// resolves it by that name when reading the key back, so renaming or moving either class makes
/// previously written keys unreadable. That is also what makes the scheme forgiving in the useful
/// direction: a ring written before the secret was configured carries no <c>encryptedKey</c> wrapper
/// at all and keeps loading unchanged.
/// </para>
/// </remarks>
public sealed class KeyProtectorXmlEncryptor(KeyProtector protector) : IXmlEncryptor {
    /// <summary>The <see cref="KeyProtector"/> purpose the ring is encrypted under.</summary>
    internal const string Purpose = "data-protection-ring";

    /// <summary>The element the key manager stores, and <see cref="KeyProtectorXmlDecryptor"/> reads.</summary>
    internal const string ElementName = "encryptedKey";

    /// <summary>The single child carrying the base64 ciphertext.</summary>
    internal const string ValueElementName = "value";

    public EncryptedXmlInfo Encrypt(XElement plaintextElement) {
        ArgumentNullException.ThrowIfNull(plaintextElement);

        // ToString() rather than the element itself: what is protected has to be the exact text the
        // decryptor will parse back, and an XElement carries namespace context that does not survive a
        // round trip through a byte array.
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        byte[] ciphertext;
        try {
            ciphertext = protector.Protect(plaintext, Purpose);
        } finally {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }

        // xmlns="" so the element is not pulled into the key manager's own namespace, which is what the
        // framework's own encryptors do and what the decryptor below matches on.
        var encrypted = new XElement(
            ElementName,
            new XAttribute("xmlns", ""),
            new XElement(ValueElementName, Convert.ToBase64String(ciphertext)));

        return new EncryptedXmlInfo(encrypted, typeof(KeyProtectorXmlDecryptor));
    }
}

/// <summary>
/// Reads back what <see cref="KeyProtectorXmlEncryptor"/> wrote. Instantiated by the framework's
/// activator, which prefers the <see cref="IServiceProvider"/> constructor — the same shape ASP.NET's
/// own <c>DpapiXmlDecryptor</c> and <c>CertificateXmlDecryptor</c> use to reach their services.
/// </summary>
/// <remarks>
/// A missing or wrong secret surfaces here as the same loud <c>CryptographicException</c> every other
/// protected row produces, and deliberately so: the alternative is a key ring that silently loses its
/// keys, which looks to an operator like every session expiring at once for no reason.
/// </remarks>
public sealed class KeyProtectorXmlDecryptor : IXmlDecryptor {
    private readonly KeyProtector _protector;

    public KeyProtectorXmlDecryptor(IServiceProvider services) {
        ArgumentNullException.ThrowIfNull(services);
        _protector = services.GetRequiredService<KeyProtector>();
    }

    public XElement Decrypt(XElement encryptedElement) {
        ArgumentNullException.ThrowIfNull(encryptedElement);

        var value = encryptedElement.Element(KeyProtectorXmlEncryptor.ValueElementName)?.Value;
        if (string.IsNullOrWhiteSpace(value))
            throw new System.Security.Cryptography.CryptographicException(
                "A data-protection key row is missing its encrypted value.");

        var plaintext = _protector.Unprotect(
            Convert.FromBase64String(value), KeyProtector.AesGcmV1, KeyProtectorXmlEncryptor.Purpose);
        try {
            return XElement.Parse(Encoding.UTF8.GetString(plaintext), LoadOptions.PreserveWhitespace);
        } finally {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
