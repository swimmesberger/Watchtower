namespace Watchtower.Application.Services.Acme;

/// <summary>
/// How the ACME traffic gets onto the wire. A seam, and only a seam: the shipped implementation is one
/// line over <see cref="AcmeClient.CreateAcmeHttpClient"/>.
/// </summary>
/// <remarks>
/// It exists because the end-to-end tests drive the real issuance flow against an in-process CA, which
/// is reachable only through that test server's own <see cref="HttpMessageHandler"/>. The alternative —
/// binding a real socket for the fake CA — would make the suite depend on a free port and on the
/// machine's networking, for no gain in what is being tested.
/// </remarks>
public interface IAcmeTransportFactory {
    /// <summary>A client for one CA. The caller owns and disposes it.</summary>
    HttpClient Create(string? caBundlePath, TimeSpan timeout);
}

/// <summary>The shipped transport: a real <see cref="HttpClient"/>, optionally trusting extra roots.</summary>
public sealed class AcmeTransportFactory : IAcmeTransportFactory {
    public HttpClient Create(string? caBundlePath, TimeSpan timeout) =>
        AcmeClient.CreateAcmeHttpClient(caBundlePath, timeout);
}
