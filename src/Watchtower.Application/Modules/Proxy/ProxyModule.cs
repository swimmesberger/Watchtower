using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Modules.Proxy;

/// <summary>
/// Reverse-proxy module: manages public <c>Route</c>s (domain → service) served by the built-in Caddy
/// proxy with automatic TLS. Handlers are exposed as <c>proxy.*</c> JSON-RPC methods.
/// </summary>
/// <remarks>
/// Exposes the <c>apps-portal</c> client flag (ADR-0030): true for a signed-in account outside the
/// operator realm, which is the SPA's cue to render the "Your applications" landing page instead of a
/// management UI that would answer Forbidden to everything (docs/central-auth/design.md §13). It is
/// declared here because the portal <em>is</em> the routes this module owns, seen from the visitor's side —
/// so an instance with no reverse proxy exposes no portal, consistent with how the rest of the module
/// disappears. Resolved by <c>Services.WatchtowerFeatureFlagService</c>; the enforcement behind it is
/// <c>SystemRealmAuthorizer</c>, never this.
/// <para>
/// The consequence of that gating, stated plainly: with <c>Modules:Proxy:Enabled</c> off the flag is
/// absent, so a realm account falls back to the management shell every screen of which answers Forbidden.
/// That fallback is <em>safe</em> — an operator never loses their UI to a disabled module, which is the
/// failure that would matter — but it is not graceful for a realm user, and it is the reason the polarity
/// is "show the portal" rather than "is an operator".
/// </para>
/// </remarks>
[AppModule("Proxy")]
[ClientFeatures("apps-portal")]
public static partial class ProxyModule {
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => ProxyJsonContext.Default;
}
