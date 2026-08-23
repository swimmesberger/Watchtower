using System.Net.Security;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.Yarp;

namespace Watchtower.Api.Proxy;

/// <summary>
/// The in-process reverse proxy's TLS listener — ADR-0022. A dedicated Kestrel endpoint that
/// picks its certificate per connection from the SNI name, so one process can terminate TLS for every
/// routed domain without a certificate being known at startup.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is defined by <em>configuration</em> rather than by a <c>Listen</c> call, and the
/// configuration it is defined by is
/// <see cref="ProxyIngressKestrelConfiguration"/>'s projection — which derives the proxy endpoints from
/// the reverse-proxy settings instead of reading whatever an operator wrote under
/// <c>Kestrel:Endpoints:ProxyHttps</c>. So the listener follows the provider: it comes up when the
/// in-process proxy is enabled and goes away when it is disabled or another provider is selected, with no
/// restart. The section is handed to Kestrel with <c>reloadOnChange: true</c>, which is what makes the
/// callback below run again each time the endpoint is (re-)added.
/// </para>
/// <para>
/// The TLS hook is <see cref="TlsHandshakeCallbackOptions"/> and not the simpler
/// <c>ServerCertificateSelector</c>: the selector hands <c>SslStream</c> a bare leaf, which then has to
/// assemble a chain out of the machine's trust store, and the shipped container has no reason to hold
/// Let's Encrypt's intermediates. The callback lets us supply a whole
/// <see cref="System.Net.Security.SslStreamCertificateContext"/> — leaf plus the intermediates we were
/// issued — which is what <see cref="CertificateStore"/> builds. That hook only exists on a
/// <c>ListenOptions</c>, which is why the endpoint is reached through the named-endpoint loader.
/// </para>
/// <para>
/// The store is resolved here — at Kestrel-configure time, and again each time the endpoint is re-added
/// by a reload — but it is <em>filled</em> earlier, by <c>Program.InitializeDatabaseAsync</c>, before the
/// server starts, and kept current by the change signal from then on. Nothing below may touch the
/// database: neither this callback nor the handshake one may, because the latter runs on the connection
/// path, where a query per handshake would be both a latency floor and a way for a scanner to make the
/// process talk to PostgreSQL.
/// </para>
/// </remarks>
internal static class ProxyHttpsEndpoint {
    /// <summary>The Kestrel endpoint name — <c>Endpoints:ProxyHttps</c> in the projected section.</summary>
    public const string EndpointName = ProxyIngressKestrelConfiguration.HttpsEndpointName;

    /// <summary>
    /// The plain-HTTP ingress endpoint's name. It needs no Kestrel code of its own; the loader binds it
    /// like any other HTTP endpoint. It is named here because the dispatcher and the ACME self-check both
    /// have to be able to tell it from the management endpoint.
    /// </summary>
    public const string HttpEndpointName = ProxyIngressKestrelConfiguration.HttpEndpointName;

    /// <summary>
    /// Points Kestrel at the projected section and attaches the SNI callback to its TLS endpoint. Called
    /// before <c>Build()</c>, unconditionally: whether either ingress listener exists is
    /// <paramref name="kestrelSection"/>'s decision, now and on every later reload.
    /// </summary>
    /// <remarks>
    /// Both halves are unconditional on purpose, because "no proxy endpoints right now" is no longer the
    /// same as "no proxy endpoints ever":
    /// <list type="bullet">
    /// <item>HTTPS configuration support is registered even for a host that starts with nothing bound —
    /// <c>CreateSlimBuilder</c> leaves it out, and the loader refuses an <c>https://</c> endpoint before
    /// ever reaching our callback. It has to be in place before the reload that adds one.</item>
    /// <item>The loader replaces Kestrel's default one even when the projection carries no endpoints at
    /// all. A section without an <c>Endpoints</c> block leaves the hosting URLs
    /// (<c>ASPNETCORE_URLS</c>, launchSettings) binding exactly as they did — verified by
    /// <c>ProxyIngressEndpointReloadTests</c> — so development, Aspire and the integration tests boot
    /// unchanged while still being able to gain an endpoint later.</item>
    /// </list>
    /// </remarks>
    public static void Configure(WebApplicationBuilder builder, IConfiguration kestrelSection) {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(kestrelSection);

        builder.WebHost.UseKestrelHttpsConfiguration();

        builder.WebHost.ConfigureKestrel((_, kestrel) => {
            var loader = kestrel.Configure(kestrelSection, reloadOnChange: true);
            loader.Endpoint(EndpointName, endpoint => {
                var services = kestrel.ApplicationServices;
                var store = services.GetRequiredService<CertificateStore>();
                var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(ProxyHttpsEndpoint));

                // h2 as well as HTTP/1.1: the endpoint fronts arbitrary web applications, and the ALPN
                // list below is what actually decides the protocol per connection.
                endpoint.ListenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                endpoint.ListenOptions.UseHttps(new TlsHandshakeCallbackOptions {
                    // A domain nothing is held for gets no handshake at all — answering with some other
                    // site's certificate is the one outcome worth ruling out. Thrown rather than returned
                    // as a null context, which reaches SslStream as a NotSupportedException and lands in
                    // the log as an error with a stack trace: this endpoint faces the open internet, where
                    // an SNI nobody has a certificate for is a scanner, not an incident. Kestrel treats an
                    // AuthenticationException as the ordinary failed handshake it is and logs it at Debug.
                    OnConnection = connection => {
                        var certificate = store.SelectContext(connection.ClientHelloInfo.ServerName)
                            ?? throw new AuthenticationException(
                                $"No certificate is held for {Quote(connection.ClientHelloInfo.ServerName)}.");
                        return ValueTask.FromResult(new SslServerAuthenticationOptions {
                            ServerCertificateContext = certificate,
                            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                        });
                    },
                    // The callback is a dictionary lookup, so a handshake that takes longer than this is a
                    // client that is not really talking TLS.
                    HandshakeTimeout = TimeSpan.FromSeconds(10),
                });

                // Logged on every (re-)add, not only at startup: the endpoint now appears and disappears
                // with the settings, and "when did TLS ingress come up?" is the question this answers.
                logger.LogInformation(
                    "Proxy HTTPS endpoint {Url} configured; serving {Count} certificate(s) from the "
                    + "database.", endpoint.ConfigSection["Url"], store.Entries.Count);
            });
        });
    }

    /// <summary>
    /// Renders an SNI name for a log message. It is attacker-controlled and arrives before anything has
    /// authenticated, so it is capped and stripped of everything that is not plausibly part of a host
    /// name — a log line is not the place to find out what a scanner put in the field.
    /// </summary>
    private static string Quote(string? sni) {
        if (string.IsNullOrEmpty(sni)) return "no SNI name";
        var capped = sni.Length > 64 ? sni[..64] : sni;
        var safe = string.Create(capped.Length, capped, (span, source) => {
            for (var i = 0; i < source.Length; i++) {
                var c = source[i];
                span[i] = char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' ? c : '?';
            }
        });
        return sni.Length > 64 ? $"'{safe}' (truncated)" : $"'{safe}'";
    }
}
