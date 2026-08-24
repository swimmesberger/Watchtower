using System.Text;
using Sodium;

namespace Watchtower.Application.Services;

/// <summary>
/// Seals a value for GitHub's Actions secrets API with libsodium's anonymous sealed box
/// (<c>crypto_box_seal</c>) — the only encryption format the API accepts. The recipient key is
/// the repo's Actions public key; only GitHub can open the box.
/// </summary>
public static class GitHubSecretSealer {
    /// <summary>Returns the base64 sealed box of <paramref name="value"/> for the given base64 public key.</summary>
    public static string Seal(string publicKeyBase64, string value) =>
        Convert.ToBase64String(
            SealedPublicKeyBox.Create(Encoding.UTF8.GetBytes(value), Convert.FromBase64String(publicKeyBase64)));
}
