using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Modules.Users;

/// <summary>
/// Administration of the local Watchtower accounts that the access-control plane authenticates
/// against (docs/central-auth/design.md §7). Every handler is <c>[RequireRole("Admin")]</c>: this is
/// the module that hands out the Admin role, so it can only ever be driven by someone who already
/// holds it.
/// </summary>
/// <remarks>
/// Gated by <c>Modules:Users:Enabled</c> like every module — turning it off removes user management
/// from a deployment without removing the accounts it manages. The frontend's <c>Users</c> module
/// keys its sidebar entry and route off the same capability snapshot flag.
/// </remarks>
[AppModule("Users")]
public static partial class UsersModule {
    /// <summary>Returns the JSON type info resolver for Users module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => UsersJsonContext.Default;
}
