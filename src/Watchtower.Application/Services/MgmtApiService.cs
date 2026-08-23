using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Modules.Tenancy;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

// ── Wire DTOs ────────────────────────────────────────────────────────────────
// Everything a granted stack is allowed to learn about the template it manages. Deliberately absent
// everywhere, exactly as in the App API: deploy output, environment variable values, App API tokens,
// credentials, and any stack not reachable through a grant.

/// <summary>A template the calling stack may manage (<c>GET /api/mgmt/templates</c>).</summary>
/// <param name="Id">Watchtower's numeric id for the template.</param>
/// <param name="Name">Operator-visible template name.</param>
/// <param name="DomainPattern">Domain template tenants are placed under, with a <c>{tenant}</c> placeholder.</param>
/// <param name="TargetServiceName">Compose service each tenant's route forwards to.</param>
/// <param name="AllowDelete">Whether this caller's grant also permits deprovisioning tenants.</param>
/// <param name="TenantCount">How many tenants the template currently has.</param>
public sealed record MgmtTemplateDto(
    int Id, string Name, string DomainPattern, string TargetServiceName, bool AllowDelete, int TenantCount);

/// <summary>The templates the calling stack has a grant on.</summary>
/// <param name="Templates">Granted templates, ordered by id.</param>
public sealed record MgmtTemplatesDto(IReadOnlyList<MgmtTemplateDto> Templates);

/// <summary>One tenant of a managed template.</summary>
/// <param name="Slug">Tenant identifier within the template.</param>
/// <param name="StackId">Watchtower's numeric id for the tenant's stack.</param>
/// <param name="Domain">Primary domain the tenant is served on; null before its route exists.</param>
/// <param name="LastDeployStatus">Status of its last deploy: <c>queued|running|success|failed</c>, or null before the first.</param>
/// <param name="LastDeployedAt">When that deploy reached a terminal state.</param>
/// <param name="CreatedAt">When the tenant was provisioned.</param>
public sealed record MgmtTenantDto(
    string Slug,
    int StackId,
    string? Domain,
    string? LastDeployStatus,
    DateTimeOffset? LastDeployedAt,
    DateTimeOffset CreatedAt);

/// <summary>A managed template's tenants, newest first.</summary>
/// <param name="Tenants">The tenants.</param>
public sealed record MgmtTenantsDto(IReadOnlyList<MgmtTenantDto> Tenants);

/// <summary>
/// One tenant of a managed template that the asserted user may enter
/// (<c>GET …/tenants/accessible</c>). No <c>current</c> flag: a management stack is not one of the tenants
/// it lists, so the field would have nothing to be true of.
/// </summary>
/// <param name="Slug">Tenant identifier within the template.</param>
/// <param name="Domain">Primary domain that tenant is served on.</param>
public sealed record MgmtAccessibleTenantDto(string Slug, string Domain);

/// <summary>The managed template's tenants the asserted user may enter, by slug ascending.</summary>
/// <param name="Tenants">The accessible tenants.</param>
public sealed record MgmtAccessibleTenantsDto(IReadOnlyList<MgmtAccessibleTenantDto> Tenants);

/// <summary>A deploy that was accepted onto the queue.</summary>
/// <param name="Id">Deploy event id tracking it.</param>
/// <param name="Status">One of <c>queued</c> or <c>running</c>.</param>
public sealed record MgmtDeployRefDto(int Id, string Status);

/// <summary>A freshly provisioned tenant (<c>POST …/tenants</c>).</summary>
/// <param name="Slug">The normalized tenant slug that was created.</param>
/// <param name="StackId">Watchtower's numeric id for the new stack.</param>
/// <param name="Domain">Domain rendered from the template's pattern.</param>
/// <param name="Deploy">The initial deploy, already queued.</param>
public sealed record MgmtTenantCreatedDto(string Slug, int StackId, string? Domain, MgmtDeployRefDto Deploy);

/// <summary>Deployment and live runtime status of one tenant (<c>GET …/tenants/{slug}</c>).</summary>
/// <param name="Slug">Tenant identifier within the template.</param>
/// <param name="Domain">Primary domain the tenant is served on.</param>
/// <param name="LastDeployStatus">Status of its last deploy, or null before the first.</param>
/// <param name="LastDeployedAt">When that deploy reached a terminal state.</param>
/// <param name="LastDeployedCommit">Commit SHA checked out by the last successful deploy.</param>
/// <param name="ActiveDeploy">The in-flight deploy, or null when nothing is queued or running.</param>
/// <param name="Services">Live container state per compose service — the same shape the App API reports.</param>
public sealed record MgmtTenantStatusDto(
    string Slug,
    string? Domain,
    string? LastDeployStatus,
    DateTimeOffset? LastDeployedAt,
    string? LastDeployedCommit,
    AppActiveDeployDto? ActiveDeploy,
    IReadOnlyList<AppServiceStatusDto> Services);

/// <summary>Acknowledgement of a redeploy request (<c>POST …/deploy</c>).</summary>
/// <param name="Deploy">The deploy that was queued.</param>
public sealed record MgmtDeployAcceptedDto(MgmtDeployRefDto Deploy);

/// <summary>Acknowledgement of a deprovision (<c>DELETE …/tenants/{slug}</c>).</summary>
/// <param name="Slug">The tenant that was removed.</param>
/// <param name="Deleted">Always true — a failure is reported as a status code, not as <c>false</c>.</param>
public sealed record MgmtTenantDeletedDto(string Slug, bool Deleted);

/// <summary>Body of a tenant-creation request.</summary>
/// <param name="Slug">Requested tenant slug; normalized and validated server-side.</param>
/// <param name="Env">
/// Per-tenant environment overrides as a JSON object map, merged over the template's base env vars.
/// Optional. Values are write-only — no management response ever reads an environment value back.
/// </param>
public sealed record MgmtCreateTenantRequest(string? Slug, IReadOnlyDictionary<string, string>? Env);

// ── Resolution results ───────────────────────────────────────────────────────

/// <summary>A resolved grant: the caller's permission on one template.</summary>
/// <param name="TemplateId">The managed template.</param>
/// <param name="AllowDelete">Whether the grant permits deprovisioning.</param>
public sealed record MgmtGrant(int TemplateId, bool AllowDelete);

/// <summary>
/// A resolved tenant of a managed template, carrying the synthesized <see cref="AppApiCaller"/> that
/// every Docker-facing read is scoped by.
/// </summary>
/// <param name="Caller">The tenant stack, in the shape the App API read models take.</param>
/// <param name="Slug">Normalized tenant slug.</param>
/// <param name="Domain">Primary domain, when the tenant has a route.</param>
public sealed record MgmtTenant(AppApiCaller Caller, string Slug, string? Domain);

/// <summary>Outcome of a management-API tenant creation.</summary>
/// <param name="Status">Whether the tenant was created, and why not when it wasn't.</param>
/// <param name="Created">The created tenant; null on failure.</param>
/// <param name="Error">Caller-facing message; null on success.</param>
public sealed record MgmtCreateResult(
    TenantProvisionStatus Status, MgmtTenantCreatedDto? Created = null, string? Error = null);

// ── Service ──────────────────────────────────────────────────────────────────

/// <summary>
/// Backs the public management API (<c>/api/mgmt/*</c>): the surface a vendor's central management UI
/// uses to run the tenants of a template it has been granted, authenticating with nothing but its own
/// stack's App API token.
/// </summary>
/// <remarks>
/// <para>
/// Three invariants hold throughout. <em>Authentication</em> is the App API's, unchanged — the same
/// token, the same 401/403 semantics, so a stack whose App API is switched off loses both surfaces at
/// once. <em>Authorization</em> is the grant row and nothing else: every entry point resolves
/// <c>(callerStackId, templateId)</c> first, and a missing grant is reported as 404 rather than 403 so
/// the surface never confirms that a template it will not serve exists. <em>Scope</em> is the template
/// id from that grant: tenant lookups are always <c>WHERE TemplateId == templateId AND TenantSlug ==
/// slug</c>, never by stack id, so a slug belonging to another template simply does not resolve.
/// </para>
/// <para>
/// Container and log access goes through an <see cref="AppApiCaller"/> synthesized from the resolved
/// tenant stack, which means every existing compose-project scoping rule applies to it unchanged: ids
/// are never taken from the request, and a tenant's containers are found through its own project label.
/// The tenant's own <c>AppApiEnabled</c> flag is deliberately not consulted — it governs whether that
/// stack may call the App API about itself, not whether its operator-granted manager may observe it.
/// </para>
/// <para>
/// <b>Realms (docs/central-auth/design.md §13) do not reach this surface in v1.</b> Its principal is a
/// <em>stack</em> proven by an App API token, not an account, so the system-realm gate on the JSON-RPC
/// surface (<see cref="SystemRealmAuthorizer"/>) has nothing to evaluate here, and the grant row remains
/// the whole of the authorization. Scoping a management principal to its own realm — so a product's
/// management stack cannot provision into another population even if a grant said it could — is the §13
/// follow-up; the seam is the grant, which would gain the realm consistency check that
/// <c>proxy.setAccess</c> already applies to route grants.
/// </para>
/// </remarks>
/// <param name="db">Scoped Watchtower database context.</param>
/// <param name="appApi">The App API service: token authentication and the shared read models.</param>
/// <param name="deployQueue">Deploy queue redeploy requests are placed on.</param>
/// <param name="provisioning">Shared tenant provisioning (also behind <c>templates.addTenant</c>).</param>
/// <param name="teardown">Shared tenant teardown (also behind <c>templates.removeTenant</c>).</param>
public sealed partial class MgmtApiService(
    WatchtowerDbContext db,
    AppApiService appApi,
    DeployQueueService deployQueue,
    TenantProvisioningService provisioning,
    TenantTeardownService teardown) {
    /// <summary>What a redeploy requested through the management API is recorded as.</summary>
    public const string DeployTrigger = "mgmt-deploy";

    /// <summary>
    /// Authenticates a <c>/api/mgmt/*</c> request from its raw <c>Authorization</c> header, with exactly
    /// the App API's semantics — the credential is the caller stack's own <c>WATCHTOWER_APP_TOKEN</c>.
    /// </summary>
    /// <param name="authorizationHeader">Raw <c>Authorization</c> header value; may be null or empty.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The authentication outcome and, on success, the calling stack.</returns>
    public Task<AppApiAuthResult> AuthenticateAsync(string? authorizationHeader, CancellationToken ct) =>
        appApi.AuthenticateAsync(authorizationHeader, ct);

    /// <summary>
    /// Resolves the calling stack's grant on one template.
    /// </summary>
    /// <remarks>
    /// The single authorization decision of the whole surface. A null result must always be answered
    /// with 404 — never 403 and never a different message for "template does not exist" versus "you
    /// have no grant on it", or the surface would become an oracle for template ids.
    /// </remarks>
    /// <param name="callerStackId">The authenticated stack.</param>
    /// <param name="templateId">Template the request is addressed to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The grant, or null when the caller has none (including when no such template exists).</returns>
    public async Task<MgmtGrant?> ResolveGrantAsync(int callerStackId, int templateId, CancellationToken ct) {
        var grant = await db.TemplateManagementGrants.AsNoTracking()
            .Where(g => g.StackId == callerStackId && g.TemplateId == templateId)
            .Select(g => new { g.TemplateId, g.AllowDelete })
            .FirstOrDefaultAsync(ct);
        return grant is null ? null : new MgmtGrant(grant.TemplateId, grant.AllowDelete);
    }

    /// <summary>Lists the templates the calling stack has been granted management of.</summary>
    /// <param name="callerStackId">The authenticated stack.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Granted templates with the caller's own capability and each template's tenant count.</returns>
    public async Task<MgmtTemplatesDto> ListTemplatesAsync(int callerStackId, CancellationToken ct) {
        var rows = await db.TemplateManagementGrants.AsNoTracking()
            .Where(g => g.StackId == callerStackId)
            .OrderBy(g => g.TemplateId)
            .Select(g => new {
                g.TemplateId,
                g.AllowDelete,
                g.Template!.Name,
                g.Template.DomainPattern,
                g.Template.TargetServiceName,
                TenantCount = db.Stacks.Count(s => s.TemplateId == g.TemplateId),
            })
            .ToListAsync(ct);

        return new MgmtTemplatesDto(rows
            .Select(r => new MgmtTemplateDto(
                r.TemplateId, r.Name, r.DomainPattern, r.TargetServiceName, r.AllowDelete, r.TenantCount))
            .ToList());
    }

    /// <summary>
    /// Lists a managed template's tenants, newest first.
    /// </summary>
    /// <param name="templateId">Template to list, already established as granted.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The template's tenants with their domain and last-deploy status.</returns>
    public async Task<MgmtTenantsDto> ListTenantsAsync(int templateId, CancellationToken ct) {
        // A tenant is a stack with both a template and a slug; the slug filter is not defensive padding
        // but part of what "tenant" means, and it keeps a slugless row from being reported as one whose
        // slug is the empty string — an identifier no other endpoint here would resolve.
        // Newest first, with the identity column breaking a shared CreatedAt.
        var stacks = await db.Stacks.AsNoTracking()
            .Where(s => s.TemplateId == templateId && s.TenantSlug != null)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.TenantSlug, s.LastDeployStatus, s.LastDeployedAt, s.CreatedAt })
            .ToListAsync(ct);

        var stackIds = stacks.Select(s => s.Id).ToList();
        var domains = await db.Routes.AsNoTracking()
            .Where(r => r.StackId != null && stackIds.Contains(r.StackId.Value) && r.IsPrimary)
            .Select(r => new { StackId = r.StackId!.Value, r.Domain })
            .ToListAsync(ct);
        var domainByStack = domains
            .GroupBy(x => x.StackId)
            .ToDictionary(g => g.Key, g => g.First().Domain);

        return new MgmtTenantsDto(stacks
            .Select(s => new MgmtTenantDto(
                s.TenantSlug!,
                s.Id,
                domainByStack.GetValueOrDefault(s.Id),
                AppApiService.MapDeployStatus(s.LastDeployStatus),
                s.LastDeployedAt,
                s.CreatedAt))
            .ToList());
    }

    /// <summary>
    /// Resolves one tenant of a managed template.
    /// </summary>
    /// <remarks>
    /// Always constrained by <paramref name="templateId"/>, which came from the grant check — a slug that
    /// names a tenant of a different template resolves to null, not to that other template's stack. An
    /// unusable slug is treated the same way: normalization is what produced the stored value, so a slug
    /// that does not normalize cannot name one.
    /// </remarks>
    /// <param name="templateId">Template the tenant must belong to.</param>
    /// <param name="slug">Tenant slug from the request path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tenant, or null when the template has no such tenant.</returns>
    public async Task<MgmtTenant?> ResolveTenantAsync(int templateId, string? slug, CancellationToken ct) {
        var normalized = TenancyMapping.NormalizeSlug(slug);
        if (normalized is null) return null;

        var stack = await db.Stacks.AsNoTracking()
            .Where(s => s.TemplateId == templateId && s.TenantSlug == normalized)
            .Select(s => new {
                s.Id, s.Name, s.ComposeProjectName, s.TenantSlug,
                s.LastDeployStatus, s.LastDeployedAt, s.LastDeployedCommit,
            })
            .FirstOrDefaultAsync(ct);
        if (stack is null) return null;

        var domain = await db.Routes.AsNoTracking()
            .Where(r => r.StackId == stack.Id && r.IsPrimary)
            .Select(r => r.Domain)
            .FirstOrDefaultAsync(ct);

        return new MgmtTenant(
            new AppApiCaller(
                stack.Id, stack.Name, stack.ComposeProjectName, stack.TenantSlug,
                stack.LastDeployStatus, stack.LastDeployedAt, stack.LastDeployedCommit),
            normalized,
            domain);
    }

    /// <summary>
    /// Provisions a tenant of a managed template from a management-API request body.
    /// </summary>
    /// <remarks>
    /// The environment map is validated here rather than in the shared provisioning service, because the
    /// trust boundary is here: keys become lines in the generated compose <c>.env</c>, so a key carrying
    /// a newline or an <c>=</c> would let a caller write additional assignments into that file. Operators
    /// writing env vars through the admin API are inside the boundary; a vendor's management UI is not.
    /// </remarks>
    /// <param name="templateId">Template to instantiate, already established as granted.</param>
    /// <param name="request">Parsed request body; a null body is treated as a missing slug.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created tenant and its queued deploy, or why it was refused.</returns>
    public async Task<MgmtCreateResult> CreateTenantAsync(
        int templateId, MgmtCreateTenantRequest? request, CancellationToken ct) {
        var env = new List<TemplateEnvVarInput>();
        foreach (var (key, value) in request?.Env ?? new Dictionary<string, string>()) {
            if (!EnvKeyPattern().IsMatch(key))
                return new MgmtCreateResult(TenantProvisionStatus.Validation,
                    Error: $"Environment variable name '{key}' is not a valid name "
                           + "(letters, digits and underscores, not starting with a digit).");
            env.Add(new TemplateEnvVarInput(key, value ?? string.Empty));
        }

        var result = await provisioning.ProvisionAsync(templateId, request?.Slug, env, ct);
        if (result.Status != TenantProvisionStatus.Created)
            return new MgmtCreateResult(result.Status, Error: result.Error);

        var tenant = result.Tenant!;
        return new MgmtCreateResult(
            TenantProvisionStatus.Created,
            new MgmtTenantCreatedDto(
                tenant.TenantSlug,
                tenant.StackId,
                tenant.Domain,
                new MgmtDeployRefDto(result.DeployEventId, result.DeployStatus!)));
    }

    /// <summary>Live status of one tenant, read through the App API's own status read model.</summary>
    /// <param name="tenant">The resolved tenant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Last-deploy metadata, the in-flight deploy if any, and per-service container state.</returns>
    public async Task<MgmtTenantStatusDto> GetTenantStatusAsync(MgmtTenant tenant, CancellationToken ct) {
        var status = await appApi.GetStatusAsync(tenant.Caller, ct);
        return new MgmtTenantStatusDto(
            tenant.Slug,
            tenant.Domain,
            status.LastDeployStatus,
            status.LastDeployedAt,
            status.LastDeployedCommit,
            status.ActiveDeploy,
            status.Services);
    }

    /// <summary>
    /// Recent deploy events for one tenant, newest first. Reuses the App API projection, which never
    /// selects the captured command output.
    /// </summary>
    /// <param name="tenant">The resolved tenant.</param>
    /// <param name="limit">Requested page size; null uses the App API default, values are clamped to its cap.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The requested page of deploy events.</returns>
    public Task<AppDeploymentsDto> GetDeploymentsAsync(MgmtTenant tenant, int? limit, CancellationToken ct) =>
        appApi.GetDeploymentsAsync(tenant.Caller, limit, ct);

    /// <summary>
    /// Lists one tenant's containers, resolved server-side from its compose project label.
    /// </summary>
    /// <param name="tenant">The resolved tenant.</param>
    /// <param name="service">Optional <c>com.docker.compose.service</c> value to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tenant's containers, in all states.</returns>
    public Task<IReadOnlyList<DockerContainerInfo>> ListTenantContainersAsync(
        MgmtTenant tenant, string? service, CancellationToken ct) =>
        appApi.ListMemberContainersAsync(tenant.Caller, service, ct);

    /// <summary>Queues a redeploy of one tenant.</summary>
    /// <param name="tenant">The resolved tenant.</param>
    /// <returns>The deploy event tracking it, already queued or running.</returns>
    public MgmtDeployAcceptedDto EnqueueDeploy(MgmtTenant tenant) {
        var enqueued = deployQueue.Enqueue(tenant.Caller.StackId, DeployTrigger);
        return new MgmtDeployAcceptedDto(new MgmtDeployRefDto(enqueued.DeployEventId, enqueued.Status));
    }

    /// <summary>
    /// Deprovisions one tenant of a managed template. The caller's <see cref="MgmtGrant.AllowDelete"/>
    /// must already have been checked — this method does not re-check it.
    /// </summary>
    /// <param name="templateId">Template the tenant belongs to, from the grant check.</param>
    /// <param name="slug">Tenant slug from the request path.</param>
    /// <param name="removeVolumes">When true the tenant's named volumes are destroyed too.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether the tenant was removed, and why not when it wasn't.</returns>
    public Task<TenantTeardownResult> DeleteTenantAsync(
        int templateId, string? slug, bool removeVolumes, CancellationToken ct) =>
        teardown.TeardownAsync(templateId, slug, removeVolumes, ct);

    /// <summary>The POSIX shape of an environment variable name — the only keys this surface accepts.</summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvKeyPattern();
}
