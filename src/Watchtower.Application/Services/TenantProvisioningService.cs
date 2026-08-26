using System.Collections.Frozen;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchtower.Application.Config;
using Watchtower.Application.Entities;
using Watchtower.Application.Modules.Tenancy;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>Why a tenant could not be provisioned, or that it was.</summary>
public enum TenantProvisionStatus {
    /// <summary>The tenant stack, its env vars and its route were created and a deploy was enqueued.</summary>
    Created,
    /// <summary>No template with the requested id exists.</summary>
    TemplateNotFound,
    /// <summary>The request itself is malformed — an unusable slug, or duplicate env keys.</summary>
    Validation,
    /// <summary>
    /// The request is well-formed but collides with something that already exists: the slug, the
    /// derived stack name, the derived domain, or the derived compose project name.
    /// </summary>
    Conflict,
}

/// <summary>Outcome of <see cref="TenantProvisioningService.ProvisionAsync"/>.</summary>
/// <param name="Status">Whether the tenant was created, and why not when it wasn't.</param>
/// <param name="Tenant">The created tenant; null unless <paramref name="Status"/> is <see cref="TenantProvisionStatus.Created"/>.</param>
/// <param name="DeployEventId">Id of the deploy event tracking the initial deploy; 0 on failure.</param>
/// <param name="DeployStatus">Enqueue status of that deploy (<c>running</c> or <c>queued</c>); null on failure.</param>
/// <param name="Error">Operator/caller-facing message; null on success.</param>
public sealed record TenantProvisionResult(
    TenantProvisionStatus Status,
    TenantDto? Tenant = null,
    int DeployEventId = 0,
    string? DeployStatus = null,
    string? Error = null);

/// <summary>
/// Creates a tenant from a <see cref="StackTemplate"/>: an isolated stack (its own compose project),
/// the merged environment, a managed route derived from the template's domain pattern, and the initial
/// deploy.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the operator-facing <c>templates.addTenant</c> handler and the public management API, so
/// both surfaces provision through exactly one code path. The result is a plain status/message record
/// rather than an Elarion <c>Result&lt;T&gt;</c> because the REST surface has to map the same outcomes
/// onto its own status codes without taking a dependency on the handler error model.
/// </para>
/// <para>
/// The stack, its environment and its route commit in a single transaction, and the deploy is enqueued
/// only afterwards — a half-created tenant must never reach the deploy queue. The uniqueness checks in
/// front of the transaction produce good messages, but they are not what makes creation safe: two
/// concurrent requests for the same slug both pass them, and it is the unique indexes on
/// <c>(template_id, tenant_slug)</c>, <c>stacks.name</c> and <c>routes.domain</c> that then reject the
/// loser, which is reported as a conflict rather than escaping as an unhandled write failure.
/// </para>
/// </remarks>
/// <param name="db">Scoped Watchtower database context.</param>
/// <param name="deployQueue">Deploy queue the initial deploy is enqueued on.</param>
/// <param name="selfProjects">Resolver for Watchtower's own (reserved) compose project name.</param>
/// <param name="options">Live Watchtower options; the instance name is read once, to stamp the tenant's backup directory.</param>
public sealed class TenantProvisioningService(
    WatchtowerDbContext db,
    DeployQueueService deployQueue,
    SelfProjectNameProvider selfProjects,
    IOptionsMonitor<WatchtowerOptions> options) {
    /// <summary>What the initial deploy of a freshly provisioned tenant is recorded as.</summary>
    public const string CreateTrigger = "tenant-create";

    /// <summary>
    /// Slugs no tenant may be provisioned under, because a management-API path segment already spells them.
    /// </summary>
    /// <remarks>
    /// <c>accessible</c> is here because <c>GET …/tenants/accessible</c> is a literal route, and ASP.NET
    /// routing ranks a literal segment above the <c>{slug}</c> parameter — a tenant slugged
    /// <c>accessible</c> would therefore be unreachable on the tenant-status route. Reserving the word at
    /// provisioning makes that collision impossible going forward; it deliberately does <em>not</em> touch
    /// <see cref="TenancyMapping.NormalizeSlug"/>, so any slug already stored keeps resolving exactly as it
    /// did (normalisation says what a slug <em>is</em>; this says which ones we hand out). A future literal
    /// segment under <c>…/tenants/</c> belongs in this set on the day it is added.
    /// </remarks>
    public static readonly IReadOnlySet<string> ReservedSlugs =
        FrozenSet.ToFrozenSet(["accessible"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Provisions one tenant of <paramref name="templateId"/>.
    /// </summary>
    /// <param name="templateId">Template to instantiate.</param>
    /// <param name="slug">Requested tenant slug; normalized (trimmed, lowercased) and validated here.</param>
    /// <param name="envOverrides">Per-tenant overrides merged over the template's base env vars; may be null.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created tenant and its initial deploy, or the reason it was refused.</returns>
    public async Task<TenantProvisionResult> ProvisionAsync(
        int templateId, string? slug, IReadOnlyList<TemplateEnvVarInput>? envOverrides, CancellationToken ct) {
        var normalized = TenancyMapping.NormalizeSlug(slug);
        if (normalized is null)
            return Failed(TenantProvisionStatus.Validation,
                "Slug must start with a letter or digit and contain only lowercase letters, digits, and hyphens.");
        // Checked on the normalized value, so "Accessible" is refused the same way "accessible" is.
        if (ReservedSlugs.Contains(normalized))
            return Failed(TenantProvisionStatus.Validation, $"Slug '{normalized}' is reserved.");
        if (envOverrides is { Count: > 0 } && TenancyMapping.FirstDuplicateKey(envOverrides) is { } dup)
            return Failed(TenantProvisionStatus.Validation, $"Duplicate env var key: '{dup}'");

        var template = await db.StackTemplates
            .Include(t => t.BaseEnvVars)
            .Include(t => t.Product)
            // Only so the result can name the pin the new tenant inherited — the copy itself needs the
            // id alone. A tenant created silently pinned would otherwise be reported as tracking latest.
            .Include(t => t.DefaultPinnedRelease)
            .FirstOrDefaultAsync(t => t.Id == templateId, ct);
        if (template is null)
            return Failed(TenantProvisionStatus.TemplateNotFound, $"Template {templateId} not found");

        if (await db.Stacks.AnyAsync(s => s.TemplateId == template.Id && s.TenantSlug == normalized, ct))
            return Failed(TenantProvisionStatus.Conflict, $"Tenant '{normalized}' already exists for this template.");

        var stackName = $"{template.Name}-{normalized}";
        if (await db.Stacks.AnyAsync(s => s.Name == stackName, ct))
            return Failed(TenantProvisionStatus.Conflict, $"A stack named '{stackName}' already exists.");

        var domain = TenancyMapping.RenderDomain(template.DomainPattern, normalized);
        if (await db.Routes.AnyAsync(r => r.Domain == domain, ct))
            return Failed(TenantProvisionStatus.Conflict, $"Domain '{domain}' is already routed.");

        // Tenant isolation depends on each instance owning its compose project name: sharing one
        // would let a tenant's App API token read another tenant's containers and logs — or, for
        // Watchtower's own reserved project, Watchtower's.
        var projectName = TenancyMapping.ProjectName(template.Name, normalized);
        if (await StackProjectNames.ValidateAsync(db, selfProjects, projectName, excludeStackId: null, ct)
            is { } projectNameError)
            return Failed(TenantProvisionStatus.Conflict, projectNameError);

        var stack = new Stack {
            Name = stackName,
            // By reference, never copied (ADR-0026): the copy-at-provision bug — a template's source
            // edits never reaching the tenants it had already stamped — disappears by construction.
            // BranchOverride stays null so the tenant inherits the template's own override, if any.
            ProductId = template.ProductId,
            // …and this one *is* copied, deliberately — the one field family ADR-0026 copies. A pin is
            // per-tenant policy, not shared definition: a tenant given a hotfix pin must not drag the
            // fleet with it, and moving the template's default must not silently repin a tenant somebody
            // pinned by hand. Both follow from the value being taken once, here, at provisioning
            // (design.md §Tenancy).
            PinnedReleaseId = template.DefaultPinnedReleaseId,
            ComposeProjectName = projectName,
            TemplateId = template.Id,
            TenantSlug = normalized,
            // {instance}/{product}/{tenant}, stamped once — a fleet's archives group under the product
            // they belong to, and a later rename of the stack, the product or the instance cannot move
            // bytes that are already on the storage (design.md §"Backups across tenants").
            BackupDirectory = BackupNaming.TenantDirectory(
                options.CurrentValue.Backup.ResolveInstanceName(),
                template.Product?.Name ?? template.Name,
                normalized),
            // Every Backup* field is left null: a tenant *inherits* its backup policy from the template
            // (invariant 5), which is the one thing copying would get wrong — a fleet-wide schedule
            // change has to reach the tenants that already exist, not only the next one.
            // Each tenant instance is its own stack and gets its own App API token — a tenant can
            // never read another tenant's status, logs or version.
            AppApiToken = AppApiTokens.Generate(),
            AppApiEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        try {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            db.Stacks.Add(stack);
            await db.SaveChangesAsync(ct);

            foreach (var v in TenancyMapping.MergeEnv(template.BaseEnvVars, envOverrides))
                db.StackEnvVars.Add(new StackEnvVar { StackId = stack.Id, Key = v.Key, Value = v.Value });

            db.Routes.Add(new Route {
                StackId = stack.Id,
                Domain = domain,
                ServiceName = template.TargetServiceName,
                ContainerPort = template.TargetPort,
                TlsEnabled = true,
                IsPrimary = true,
                Kind = DomainKind.Managed,
                Status = RouteStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        } catch (DbUpdateException) {
            // A concurrent request won one of the unique indexes between the checks above and this
            // write. The transaction is rolled back by its own disposal, so nothing was created.
            return Failed(TenantProvisionStatus.Conflict,
                $"Tenant '{normalized}' could not be created: the slug, domain or stack name was taken concurrently.");
        }

        // Past the commit point: the tenant exists whatever the queue does with this.
        var enqueued = deployQueue.Enqueue(stack.Id, CreateTrigger);
        return new TenantProvisionResult(
            TenantProvisionStatus.Created,
            new TenantDto(
                stack.Id, normalized, stack.Name, domain, enqueued.Status, null,
                stack.PinnedReleaseId is null ? TenancyMapping.TrackingLatest : TenancyMapping.TrackingPinned,
                TenancyMapping.ReleaseRef(template.DefaultPinnedRelease)),
            enqueued.DeployEventId,
            enqueued.Status);
    }

    private static TenantProvisionResult Failed(TenantProvisionStatus status, string error) =>
        new(status, Error: error);
}
