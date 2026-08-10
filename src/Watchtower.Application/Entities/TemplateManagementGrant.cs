namespace Watchtower.Application.Entities;

/// <summary>
/// Lets one stack manage the tenants of one <see cref="StackTemplate"/> through the public management
/// API (<c>/api/mgmt/*</c>): list, provision, inspect, redeploy and — with <see cref="AllowDelete"/> —
/// deprovision them.
/// </summary>
/// <remarks>
/// <para>
/// The credential a caller presents is the grantee stack's own App API token; this row is the entire
/// authorization. A token on its own reaches nothing beyond the self-only App API, so revoking the
/// grant (rather than rotating a token) is how an operator withdraws management rights.
/// </para>
/// <para>
/// Grants are operator-managed: only the admin-gated <c>templates.grantManagement</c> /
/// <c>templates.revokeManagement</c> handlers write them. A stack that is itself an instance of the
/// template is refused at that point — a tenant must not be able to manage the template it lives in.
/// </para>
/// </remarks>
public sealed class TemplateManagementGrant {
    public int Id { get; set; }

    /// <summary>The stack whose App API token is allowed to manage <see cref="TemplateId"/>'s tenants.</summary>
    public int StackId { get; set; }
    public Stack? Stack { get; set; }

    /// <summary>The template whose tenants the granted stack may manage.</summary>
    public int TemplateId { get; set; }
    public StackTemplate? Template { get; set; }

    /// <summary>
    /// When false the grant is read/create/deploy only: <c>DELETE /api/mgmt/.../tenants/{slug}</c> is
    /// refused with 403 and no volume can be purged. Deprovisioning destroys a tenant's data, so it is
    /// a separate decision from being allowed to manage the template at all.
    /// </summary>
    public bool AllowDelete { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
