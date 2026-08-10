using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Modules.Realms;

/// <summary>
/// Administration of the user populations Watchtower authenticates against
/// (docs/central-auth/design.md §13). A realm owns a credential space (user and group names are unique
/// within it, not across the instance), an SSO scope (its login host is where its <c>__wt_sso</c> cookie
/// lives), and the categories whose tenants its accounts may enter.
/// </summary>
/// <remarks>
/// Every handler is <c>[RequireRole("Admin")]</c>, and the whole JSON-RPC surface additionally requires a
/// system-realm principal (<see cref="Services.SystemRealmAuthorizer"/>): creating a realm is creating a
/// population, which is the most privileged act there is here — nothing else can grant access to accounts
/// that do not exist yet.
/// <para>
/// Gated by <c>Modules:Realms:Enabled</c> like every module. Turning it off hides realm administration
/// without dissolving the realms: the resolver, the access invariant and the token issuer all live in the
/// application services rather than in this module, so an instance with several populations keeps
/// enforcing the boundary between them either way.
/// </para>
/// </remarks>
[AppModule("Realms")]
public static partial class RealmsModule {
    /// <summary>Returns the JSON type info resolver for Realms module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => RealmsJsonContext.Default;
}
