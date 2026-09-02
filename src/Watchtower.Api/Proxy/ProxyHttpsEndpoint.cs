using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Watchtower.Application.Services.Acme;
using Watchtower.Application.Services.InternalCa;
using Watchtower.Application.Services.PortRoutes;
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
            // Resolved once and then held, so a handshake costs the same dictionary lookup it costs on the
            // named endpoint below — which captures the store in its own callback. Not eagerly: the
            // container does not exist when Kestrel is configured. A race here resolves the same singleton
            // twice and keeps one of them, which is not worth a lock.
            CertificateStore? store = null;
            ConfigurePortRouteTls(
                kestrel,
                () => PortRouteListeners.BoundPorts(kestrelSection),
                () => (store ??= kestrel.ApplicationServices.GetRequiredService<CertificateStore>())
                    .SelectContext(InternalCaNames.SharedLeafHost),
                () => kestrel.ApplicationServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(ProxyHttpsEndpoint)));

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
    /// The TLS half of the port-bound routes (ADR-0033): the same
    /// <see cref="TlsHandshakeCallbackOptions"/> hook the named endpoint above uses, attached from
    /// Kestrel's <em>endpoint defaults</em> to whichever listeners a port route currently owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The endpoint names are <c>ProxyPort{n}</c> and they come and go with the routes, so there is no
    /// fixed set of names a callback could be registered against — and registering one late, against a
    /// loader that is concurrently reloading, is a race. The endpoint defaults are the way out. They run
    /// for every listener as it is created, before the configuration loader does anything about TLS, and
    /// the loader's own https handling is guarded on <c>!listenOptions.IsTls</c> — which
    /// <see cref="ListenOptionsHttpsExtensions.UseHttps(ListenOptions, TlsHandshakeCallbackOptions)"/>
    /// sets. So a listener we claim here is TLS on <em>our</em> terms and the loader steps over it,
    /// exactly as it steps over the named <see cref="EndpointName"/> endpoint. A config endpoint with an
    /// <c>https://</c> URL and no <c>Certificate</c> section therefore binds without one.
    /// </para>
    /// <para>
    /// The scope is the listener's own port, decided once as the listener is created rather than per
    /// connection: a client reaching a bare LAN address sends no usable SNI, so the port is the only
    /// thing that can say which route a connection arrived on, and it is a constant for the life of that
    /// listener. The set is re-read on every listener creation, which is what makes a route added or
    /// deleted at runtime gain or lose its TLS along with its endpoint.
    /// </para>
    /// <para>
    /// It is read from the <em>projected section</em> — <see cref="PortRouteListeners.BoundPorts"/> —
    /// rather than from <see cref="YarpListenerState"/>, and that is load-bearing rather than a
    /// preference. The state is republished from a reload callback on that same section, and Kestrel's
    /// loader has its own; measured, the loader's runs first, so a listener created during a reload would
    /// see a state that has not caught up yet and would be built without TLS until something rebound it.
    /// The section is what Kestrel is reading to create the listener, so the two cannot disagree.
    /// </para>
    /// <para>
    /// <b>Every other endpoint is left exactly as it was.</b> That is the reason this is an endpoint
    /// default and not <c>ConfigureHttpsDefaults</c> with a certificate selector: a selector is applied to
    /// every HTTPS listener in the process, and Kestrel both discards a configured
    /// <c>ServerCertificate</c> in its presence and skips its default-certificate fallback because
    /// <c>HasServerCertificateOrSelector</c> is then true. An operator's own endpoint with a
    /// <c>Certificate</c> section — or one relying on <c>Kestrel:Certificates:Default</c>, or on the
    /// development certificate — would stop serving at the next rebind. Claiming individual listeners by
    /// port cannot do that: a port no route owns is never touched, and an <c>https://</c> endpoint with
    /// no certificate anywhere still fails at startup, which is the behaviour that was always there.
    /// </para>
    /// <para>
    /// Like <c>ConfigureHttpsDefaults</c>, <c>ConfigureEndpointDefaults</c> replaces what was there rather
    /// than composing, so this stays the host's only caller; anything that needs endpoint defaults of its
    /// own has to join this one.
    /// </para>
    /// </remarks>
    /// <param name="boundPorts">The ports carrying a port route's listener, re-read per listener.</param>
    /// <param name="certificate">The shared LAN leaf and its chain, or null while none is held.</param>
    /// <param name="logger">Resolved late: the container does not exist when Kestrel is configured.</param>
    internal static void ConfigurePortRouteTls(
        KestrelServerOptions kestrel,
        Func<IReadOnlySet<int>> boundPorts,
        Func<SslStreamCertificateContext?> certificate,
        Func<ILogger> logger) {
        // One line per port while the condition lasts, rather than one per connection: a listener with no
        // certificate is a standing condition, and a scanner must not be able to fill the log by dialling
        // it. Cleared again when that port serves a handshake, so an operator who fixes their LAN names
        // and later breaks them again is told the second time too.
        var refused = new ConcurrentDictionary<int, byte>();

        kestrel.ConfigureEndpointDefaults(listen => {
            // Unix sockets, named pipes and file handles have no port to match on, and no port route can
            // name one.
            if (listen.IPEndPoint is not { } address) return;
            var port = address.Port;
            if (!boundPorts().Contains(port)) return;

            // h2 as well as HTTP/1.1, stated on both halves: the listener fronts an arbitrary web
            // application, and the ALPN list below is what actually decides the protocol per connection.
            listen.Protocols = HttpProtocols.Http1AndHttp2;
            listen.UseHttps(new TlsHandshakeCallbackOptions {
                OnConnection = _ => ValueTask.FromResult(Handshake(port)),
                // The callback is a dictionary lookup, so a handshake that takes longer than this is a
                // client that is not really talking TLS.
                HandshakeTimeout = TimeSpan.FromSeconds(10),
            });
        });

        SslServerAuthenticationOptions Handshake(int port) {
            var context = certificate();
            if (context is null) {
                if (refused.TryAdd(port, 0))
                    logger().LogWarning(
                        "No LAN certificate is held; handshakes on port {Port} will fail until the LAN "
                        + "names are configured under Settings → Reverse proxy.", port);
                // Thrown rather than answered with nothing, for the reason the SNI endpoint states: an
                // AuthenticationException is the ordinary failed handshake Kestrel logs at Debug, where a
                // null context reaches SslStream as an error with a stack trace.
                throw new AuthenticationException($"No LAN certificate is held for port {port}.");
            }
            refused.TryRemove(port, out _);
            return new SslServerAuthenticationOptions {
                ServerCertificateContext = context,
                ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
            };
        }
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
