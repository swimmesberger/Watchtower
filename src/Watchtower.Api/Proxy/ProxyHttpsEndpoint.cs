using System.Net.Security;
using System.Security.Authentication;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Watchtower.Application.Services.Acme;

namespace Watchtower.Api.Proxy;

/// <summary>
/// The in-process reverse proxy's TLS listener — ADR-0020. A dedicated Kestrel endpoint that
/// picks its certificate per connection from the SNI name, so one process can terminate TLS for every
/// routed domain without a certificate being known at startup.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is defined by <em>configuration</em> (<c>Kestrel:Endpoints:ProxyHttps:Url</c>, set by the
/// shipped Dockerfile) rather than by a <c>Listen</c> call, for two reasons. It keeps the port an
/// operator's decision — the container image publishes 8443, a bespoke deployment can move it or leave it
/// out entirely — and, more importantly, an unconfigured endpoint means the callback below never runs, so
/// development, the Aspire AppHost and <c>WebApplicationFactory</c> are untouched by all of this.
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
/// </remarks>
internal static class ProxyHttpsEndpoint {
    /// <summary>The Kestrel endpoint name — <c>Kestrel:Endpoints:ProxyHttps:Url</c>.</summary>
    public const string EndpointName = "ProxyHttps";

    /// <summary>
    /// The plain-HTTP ingress endpoint's name — <c>Kestrel:Endpoints:ProxyHttp:Url</c>. It needs no
    /// Kestrel code of its own; the default loader binds it. It is named here because the two proxy
    /// endpoints share the "blank means off" handling below, and because the dispatcher and the ACME
    /// self-check both have to be able to tell it from the management endpoint.
    /// </summary>
    public const string HttpEndpointName = "ProxyHttp";

    private const string EndpointKey = $"Endpoints:{EndpointName}";
    private const string UrlKey = $"Kestrel:{EndpointKey}:Url";

    /// <summary>
    /// Adds the SNI-served HTTPS endpoint, if one is configured, and takes any blank proxy endpoint out of
    /// the configuration the Kestrel loader reads. Called before <c>Build()</c>.
    /// </summary>
    /// <remarks>
    /// Three cases, and the middle one is the reason this does anything at all when the endpoints are off.
    /// <list type="bullet">
    /// <item>No <c>ProxyHttp</c>/<c>ProxyHttps</c> configuration — every non-container deployment. Nothing
    /// is touched.</item>
    /// <item>Configured but blank. This is how an operator turns off a variable baked into an image:
    /// Docker has no way to unset one, only to override it with an empty value. Kestrel's own loader
    /// rejects an endpoint whose <c>Url</c> is present but empty and fails the host on startup, so the
    /// section is filtered out of the configuration the loader reads and the endpoint is simply
    /// absent. Both proxy endpoints get this, independently: turning ingress TLS off is a normal thing to
    /// do behind another terminator, and turning plain-HTTP ingress off is a normal thing to do when
    /// nothing publishes 80.</item>
    /// <item>Configured with a URL — the listener below for <c>ProxyHttps</c>; the default loader for
    /// <c>ProxyHttp</c>, which is an ordinary HTTP endpoint and needs nothing from us.</item>
    /// </list>
    /// </remarks>
    public static void Configure(WebApplicationBuilder builder) {
        var blank = BlankEndpointKeys(builder.Configuration);
        var url = builder.Configuration[UrlKey];

        if (string.IsNullOrWhiteSpace(url)) {
            // Genuinely absent: leave Kestrel's own configuration handling exactly as it is, rather than
            // replacing the default loader with an equivalent one for no reason.
            if (blank.Count == 0) return;

            builder.WebHost.ConfigureKestrel((context, kestrel) =>
                kestrel.Configure(Without(context.Configuration, blank), reloadOnChange: false));
            return;
        }

        // The host is built with CreateSlimBuilder, which leaves Kestrel's HTTPS configuration support
        // out; without this the loader refuses an https:// endpoint before ever reaching our callback.
        // Only on this path, so nothing about an HTTP-only host changes.
        builder.WebHost.UseKestrelHttpsConfiguration();

        builder.WebHost.ConfigureKestrel((context, kestrel) => {
            var kestrelConfiguration = blank.Count == 0
                ? context.Configuration.GetSection("Kestrel")
                : Without(context.Configuration, blank);
            var loader = kestrel.Configure(kestrelConfiguration, reloadOnChange: false);
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

                logger.LogInformation(
                    "Proxy HTTPS endpoint {Url} configured; serving {Count} certificate(s) from {CertPath}.",
                    url, store.Entries.Count, store.RootPath);
            });
        });
    }

    /// <summary>
    /// The proxy endpoints that are present in configuration but carry no URL, as section keys relative to
    /// <c>Kestrel</c> — the endpoints the loader has to be kept from seeing.
    /// </summary>
    private static IReadOnlyList<string> BlankEndpointKeys(IConfiguration configuration) => [
        .. new[] { HttpEndpointName, EndpointName }
            .Select(name => $"Endpoints:{name}")
            .Where(key => configuration.GetSection($"Kestrel:{key}").Exists() &&
                          string.IsNullOrWhiteSpace(configuration[$"Kestrel:{key}:Url"])),
    ];

    /// <summary>
    /// The <c>Kestrel</c> section with the named endpoints removed, as a standalone configuration for the
    /// loader to read. Copied key by key rather than edited in place because a configuration provider has
    /// no notion of deleting a key — only of supplying one.
    /// </summary>
    private static IConfiguration Without(IConfiguration configuration, IReadOnlyList<string> endpointKeys) {
        var kept = configuration.GetSection("Kestrel")
            .AsEnumerable(makePathsRelative: true)
            // Section markers carry no value and are implied by the keys underneath them.
            .Where(pair => pair.Value is not null)
            .Where(pair => !endpointKeys.Any(
                key => pair.Key.StartsWith($"{key}:", StringComparison.OrdinalIgnoreCase)));
        return new ConfigurationBuilder().AddInMemoryCollection(kept).Build();
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
