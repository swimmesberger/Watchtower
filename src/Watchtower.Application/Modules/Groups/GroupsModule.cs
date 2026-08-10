using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Modules.Groups;

/// <summary>
/// Administration of the named account sets that route access can be granted to
/// (docs/central-auth/design.md §3). A group is membership and nothing else: it holds no policy of its
/// own, so what an administrator does here is decide <em>who is in it</em>, while what it unlocks is
/// decided per route by the Proxy module's access handlers.
/// </summary>
/// <remarks>
/// Every handler is <c>[RequireRole("Admin")]</c>, for the same reason the Users module's are: adding an
/// account to a group is granting it every route that group is named on, so it is exactly as privileged
/// an act as writing the grant directly.
/// <para>
/// Gated by <c>Modules:Groups:Enabled</c> like every module. Turning it off hides group administration
/// without dissolving the groups: existing memberships keep being honoured by the policy evaluator, which
/// lives in the application services rather than in this module.
/// </para>
/// </remarks>
[AppModule("Groups")]
public static partial class GroupsModule {
    /// <summary>Returns the JSON type info resolver for Groups module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => GroupsJsonContext.Default;
}
