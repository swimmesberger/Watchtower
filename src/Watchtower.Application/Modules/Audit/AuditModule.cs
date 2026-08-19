using System.Text.Json.Serialization.Metadata;

namespace Watchtower.Application.Modules.Audit;

/// <summary>
/// The read-only view over the access-control plane's audit trail (docs/central-auth/design.md §3):
/// every login, denial, policy change and break-glass recovery that the rest of the application has
/// already written as an <see cref="Entities.AuthEvent"/> row.
/// </summary>
/// <remarks>
/// Deliberately a <em>reader</em> and nothing else. The trail's writers stay where the acts they record
/// happen — the login endpoints in <c>Watchtower.Api</c>, and the <c>RecordAsync</c> helpers of the Users,
/// Groups, Realms and Proxy modules — because a row written by the module that displays it would be a row
/// nobody writes once that module is switched off.
/// <para>
/// Both handlers are <c>[RequireRole("Admin")]</c>, and the trail never leaves the operator population:
/// the Admin role is only ever emitted for a system-realm account
/// (<see cref="Services.WatchtowerClaims.ForUser"/>) and the whole JSON-RPC surface additionally passes
/// <see cref="Services.SystemRealmAuthorizer"/>. The rows name accounts and apps across every realm, so
/// reading them is an instance-administration act.
/// </para>
/// <para>
/// Gated by <c>Modules:Audit:Enabled</c> like every module. Turning it off hides the view without
/// interrupting the trail — nothing here writes it.
/// </para>
/// </remarks>
[AppModule("Audit")]
public static partial class AuditModule {
    /// <summary>Returns the JSON type info resolver for Audit module types.</summary>
    public static IJsonTypeInfoResolver GetJsonTypeInfoResolver() => AuditJsonContext.Default;
}
