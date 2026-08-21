using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Modules.Audit;

/// <summary>
/// The audit surfaces, read-only both: Watchtower's general trail of what it changed
/// (<see cref="Entities.AuditEvent"/> — writes against external control planes, backup runs,
/// settings changes, read through <c>audit.listEvents</c>), and the access-control plane's trail of
/// what <em>users</em> did (docs/central-auth/design.md §3): every login, denial, policy change and
/// break-glass recovery already written as an <see cref="Entities.AuthEvent"/> row.
/// </summary>
/// <remarks>
/// Deliberately a <em>reader</em> and nothing else. The trails' writers stay where the acts they
/// record happen — <see cref="Services.AuditLog"/> callers for the general trail; the login
/// endpoints in <c>Watchtower.Api</c> and the <c>RecordAsync</c> helpers of the Users, Groups,
/// Realms and Proxy modules for the auth trail — because a row written by the module that displays
/// it would be a row nobody writes once that module is switched off.
/// <para>
/// Every handler is <c>[RequireRole("Admin")]</c>, and the trails never leave the operator
/// population: the Admin role is only ever emitted for a system-realm account
/// (<see cref="Services.WatchtowerClaims.ForUser"/>) and the whole JSON-RPC surface additionally
/// passes <see cref="Services.SystemRealmAuthorizer"/>. The rows name accounts and apps across
/// every realm, so reading them is an instance-administration act.
/// </para>
/// <para>
/// Gated by <c>Modules:Audit:Enabled</c> like every module. Turning it off hides the views without
/// interrupting either trail — nothing here writes them.
/// </para>
/// </remarks>
[AppModule("Audit")]
public static partial class AuditModule {
    /// <summary>Returns the JSON type info resolver for Audit module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => AuditJsonContext.Default;
}
