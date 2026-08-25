using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Watchtower.Application.Services.Acme;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The CA-bundle escape hatch: <c>Proxy:Yarp:AcmeCaBundlePath</c> makes an internal CA's root trusted
/// <em>in addition to</em> the system store, which is what lets Watchtower talk to a step-ca or a Pebble
/// whose root the container does not ship.
/// </summary>
/// <remarks>
/// Driven against a real TLS handshake on loopback rather than by calling the validation callback
/// directly: the property under test is that the callback is wired into the handler at all and that
/// <see cref="SslPolicyErrors"/> reaches it in the form it expects, neither of which a direct call would
/// exercise. The server is a bare <see cref="SslStream"/> answering one canned HTTP response, so there is
/// no web host to start and nothing to wait on.
/// </remarks>
public sealed class AcmeHttpClientTrustTests : IDisposable {
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "watchtower-acme-trust-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithoutABundle_ACertificateFromAnUnknownRootIsRefused() {
        using var ca = LoopbackCa.Create();
        await using var server = TlsEchoServer.Start(ca);
        using var client = AcmeClient.CreateAcmeHttpClient(caBundlePath: null, TimeSpan.FromSeconds(10));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(server.Url, Ct));

        Assert.IsType<AuthenticationException>(error.InnerException, exactMatch: false);
    }

    [Fact]
    public async Task WithTheBundle_TheSameCertificateIsAccepted() {
        using var ca = LoopbackCa.Create();
        await using var server = TlsEchoServer.Start(ca);
        using var client = AcmeClient.CreateAcmeHttpClient(WriteBundle(ca), TimeSpan.FromSeconds(10));

        var response = await client.GetAsync(server.Url, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>
    /// The bundle rescues a chain that does not reach a trusted root. It must not rescue a certificate
    /// issued for a different name.
    /// </summary>
    /// <remarks>
    /// This is the failure mode a "trust these extra roots" callback invites: written as
    /// <c>errors == None || VerifyAgainstCustomRoots(...)</c> it also swallows
    /// <see cref="SslPolicyErrors.RemoteCertificateNameMismatch"/>, so configuring a CA bundle silently
    /// turns hostname verification off for ACME traffic — and anyone holding <em>any</em> certificate the
    /// bundled root issued can then impersonate the CA. Only
    /// <see cref="SslPolicyErrors.RemoteCertificateChainErrors"/> is the bundle's to answer for.
    /// </remarks>
    [Fact]
    public async Task TheBundleDoesNotRescueAWrongName() {
        using var ca = LoopbackCa.Create(forName: "somewhere-else.invalid");
        await using var server = TlsEchoServer.Start(ca);
        using var client = AcmeClient.CreateAcmeHttpClient(WriteBundle(ca), TimeSpan.FromSeconds(10));

        // The root IS in the bundle, so the chain verifies. The name does not, and that is fatal.
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(server.Url, Ct));

        Assert.IsType<AuthenticationException>(error.InnerException, exactMatch: false);
    }

    /// <summary>
    /// Additive, not permissive: naming one root does not stop the handler checking the chain. A
    /// certificate from a <em>different</em> unknown root is still refused.
    /// </summary>
    [Fact]
    public async Task TheBundleDoesNotBlanketAcceptEverything() {
        using var trusted = LoopbackCa.Create();
        using var other = LoopbackCa.Create();
        await using var server = TlsEchoServer.Start(other);
        using var client = AcmeClient.CreateAcmeHttpClient(WriteBundle(trusted), TimeSpan.FromSeconds(10));

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(server.Url, Ct));
    }

    private string WriteBundle(LoopbackCa ca) {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "ca.pem");
        File.WriteAllText(path, ca.Root.ExportCertificatePem() + "\n");
        return path;
    }

    public void Dispose() {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>A throwaway root and a loopback server certificate issued under it.</summary>
    private sealed class LoopbackCa(X509Certificate2 root, X509Certificate2 server) : IDisposable {
        public X509Certificate2 Root { get; } = root;
        public X509Certificate2 Server { get; } = server;

        /// <param name="forName">
        /// A DNS name to issue the server certificate for instead of the loopback address — which makes
        /// it a valid certificate for the wrong host, the case the name-mismatch test needs.
        /// </param>
        public static LoopbackCa Create(string? forName = null) {
            var window = (DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootRequest = new CertificateRequest("CN=Watchtower ACME Test Root", rootKey, HashAlgorithmName.SHA256);
            rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            using var root = rootRequest.CreateSelfSigned(window.Item1, window.Item2);

            using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var serverRequest = new CertificateRequest("CN=acme-loopback", serverKey, HashAlgorithmName.SHA256);
            serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            serverRequest.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
            var sans = new SubjectAlternativeNameBuilder();
            // The address the client dials. Without an IP SAN the handshake fails on name mismatch, which
            // would make every other case here pass for the wrong reason.
            if (forName is null) sans.AddIpAddress(IPAddress.Loopback);
            else sans.AddDnsName(forName);
            serverRequest.CertificateExtensions.Add(sans.Build());
            using var issued = serverRequest.Create(
                root, window.Item1, window.Item2, RandomNumberGenerator.GetBytes(12));

            // Through PKCS#12 so the private key is attached in a form SslStream can use on every platform.
            using var withKey = issued.CopyWithPrivateKey(serverKey);
            var pfx = withKey.Export(X509ContentType.Pkcs12);
            var usable = X509CertificateLoader.LoadPkcs12(pfx, password: null);
            CryptographicOperations.ZeroMemory(pfx);

            return new LoopbackCa(X509CertificateLoader.LoadCertificate(root.RawData), usable);
        }

        public void Dispose() {
            Root.Dispose();
            Server.Dispose();
        }
    }

    /// <summary>
    /// A TLS listener that answers every connection with one fixed HTTP response. Enough for
    /// <see cref="HttpClient"/> to complete a request, and small enough that a failed handshake is
    /// unambiguously about trust rather than about the server.
    /// </summary>
    private sealed class TlsEchoServer : IAsyncDisposable {
        private static readonly byte[] Response =
            Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");

        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _loop;

        private TlsEchoServer(TcpListener listener, X509Certificate2 certificate) {
            _listener = listener;
            _loop = Task.Run(async () => {
                while (!_stopping.IsCancellationRequested) {
                    TcpClient connection;
                    try {
                        connection = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    } catch (Exception) {
                        return;
                    }
                    _ = Task.Run(async () => {
                        using (connection)
                        await using (var tls = new SslStream(connection.GetStream())) {
                            try {
                                await tls.AuthenticateAsServerAsync(certificate, false, false);
                                await DrainRequestAsync(tls, _stopping.Token);
                                await tls.WriteAsync(Response, _stopping.Token);
                                await tls.FlushAsync(_stopping.Token);
                                await tls.ShutdownAsync();
                            } catch (Exception) {
                                // A client that refused the certificate hangs up here; that is the test.
                            }
                        }
                    });
                }
            });
        }

        public string Url { get; private init; } = "";

        /// <summary>
        /// Reads the request through its header terminator before the response is sent. Closing a
        /// socket that still holds received-but-unread data aborts the connection (RST) instead of
        /// closing it, and on Windows that abort also discards the response already sent — the
        /// client then fails with "connection aborted" even though the handshake and the write both
        /// succeeded. Draining first, and closing with a TLS close_notify, keeps the close orderly
        /// on every platform.
        /// </summary>
        private static async Task DrainRequestAsync(SslStream tls, CancellationToken ct) {
            var buffer = new byte[4096];
            var request = new StringBuilder();
            while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal)) {
                var read = await tls.ReadAsync(buffer, ct);
                if (read == 0) return;
                request.Append(Encoding.ASCII.GetString(buffer, 0, read));
            }
        }

        public static TlsEchoServer Start(LoopbackCa ca) {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            return new TlsEchoServer(listener, ca.Server) { Url = $"https://127.0.0.1:{port}/" };
        }

        public async ValueTask DisposeAsync() {
            await _stopping.CancelAsync();
            _listener.Stop();
            try {
                await _loop;
            } catch (Exception) {
                // Shutting down.
            }
            _stopping.Dispose();
        }
    }
}
