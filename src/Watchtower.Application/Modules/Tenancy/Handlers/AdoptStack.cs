using Elarion.Abstractions.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;
using Watchtower.Application.Services;

namespace Watchtower.Application.Modules.Tenancy.Handlers;

/// <summary>
/// Adopts an existing standalone stack of a template's product as the tenant <c>{slug}</c>: the stack
/// keeps its containers, volumes, data, name and compose project, and gains the tenancy setup's identity
/// — a <see cref="Stack.TemplateId"/>, a <see cref="Stack.TenantSlug"/>, the template's base environment
/// for the keys it does not already define, and a managed route rendered from the domain pattern.
/// </summary>
/// <remarks>
/// <para>
/// <b>The keep-contract.</b> Adoption is the inverse of provisioning in exactly one respect: nothing is
/// created, so nothing may be recreated. The stack's <see cref="Stack.Name"/>,
/// <see cref="Stack.ComposeProjectName"/>, environment values, <see cref="Stack.PinnedReleaseId"/> and
/// <see cref="Stack.BackupDirectory"/> are all left as they are, and no deploy is enqueued —
/// <em>nothing about what the stack runs changed</em>, so redeploying it would be a restart nobody asked
/// for. That is the acceptance test for this handler.
/// </para>
/// <para>
/// <b>The naming asymmetry, kept deliberately.</b> A provisioned tenant is named
/// <c>{template}-{slug}</c> with a matching compose project; an adopted one keeps whatever it was called.
/// Renaming the compose project is the one thing that would destroy the estate this feature exists to
/// preserve — Compose namespaces containers, networks and volumes by project name, so a rename is a
/// recreate. Renaming only the stack would leave the two disagreeing for no gain. The convention
/// therefore holds for provisioned tenants and is not a rule about tenants in general
/// (docs/products/design.md §Tenancy, "Adoption"). There is deliberately no migrate-with-recreate option
/// in v1.
/// </para>
/// <para>
/// <b>The route is created, never stolen.</b> The managed route is built exactly as
/// <see cref="TenantProvisioningService"/> builds it — the rendered domain, the template's target service
/// and port, TLS on — with one difference: it is marked <see cref="Route.IsPrimary"/> only when the stack
/// has no primary route yet. A stack that has been serving a customer-owned domain has that domain on
/// every link, every bookmark and every certificate; demoting it to make room for a subdomain the
/// operator has just invented would be a redirect nobody asked for. Domains are globally unique, so a
/// rendered domain that already exists is refused before anything is written rather than moved.
/// </para>
/// <para>
/// <b>Environment: the stack is the override.</b> Provisioning merges per-tenant overrides over the
/// template's base (<see cref="TenancyMapping.MergeEnv"/>, overrides win). Adoption applies the same rule
/// with the stack's own rows in the overriding position: a template base var is copied in only when the
/// stack does not already define that key. The stack is running with its environment and its values are
/// the ones in force; silently replacing them would change what the next deploy applies, which the
/// keep-contract forbids. The keys that were added are reported back, because "your instance quietly
/// gained four environment variables" is not something to discover later.
/// </para>
/// <para>
/// <b>Version policy is untouched.</b> <see cref="StackTemplate.DefaultPinnedReleaseId"/> is documented as
/// "the release every <em>future</em> tenant of this template is pinned to" and is copied at provisioning
/// (invariant 17); an adopted stack is not a future tenant, it is a running one, so its pin — set or
/// unset — is left exactly as it is. <c>templates.setTenantsRelease</c> is how an operator brings it onto
/// the fleet's version, deliberately and with the consequence stated.
/// </para>
/// <para>
/// <b>The branch is preserved, and that needs a write.</b> A tenant inherits its template's
/// <see cref="StackTemplate.BranchOverride"/> (<see cref="ProductSourceResolver"/>), so a stack with no
/// override of its own would start deploying the template's branch the moment it is adopted — a change to
/// what it runs, which is the one thing adoption may not do. The effective branch is therefore read
/// before the write and put back through <see cref="ProductSourceResolver.OverrideFor"/> against what the
/// stack would inherit <em>after</em> adoption: null when the two agree (invariant 5 — never pin what is
/// merely inherited), an explicit override only when the template would otherwise have moved it.
/// </para>
/// <para>
/// <b>Two things adoption does change, and neither is "what it runs".</b> Backup <em>policy</em> needs no
/// rewrite: the four <c>Backup*</c> columns are tri-state, so a stack that never set them starts
/// inheriting the fleet's policy through <see cref="BackupPolicyResolver"/> the moment it has a template,
/// and one that set them by hand keeps its own values (invariant 18 — that is the tri-state paying off).
/// The archive <em>directory</em> is not recomputed: <see cref="Stack.BackupDirectory"/> is stamped once
/// and names where bytes already are (invariant 20), so an adopted tenant keeps writing where its
/// existing archives live rather than moving to <c>{instance}/{product}/{tenant}</c>.
/// </para>
/// <para>
/// <b>Who is admitted is never moved as a side effect.</b> A service route takes its realm from its
/// stack's category (<see cref="Services.RouteAccessPolicy"/>), so adopting into a template of a
/// non-system realm would silently re-point every <em>protected</em> route the stack already serves at
/// another population: the accounts using them today would stop being admitted on their next request,
/// and the new realm's accounts would be let in without anybody having granted them anything. That is
/// word for word why <c>templates.update</c> refuses to move a populated template's realm, and it
/// applies here unchanged — so this refuses too, naming the routes and the way through. A route in
/// <see cref="AccessMode.Public"/> is untouched by the move (public admits everyone in every realm), so
/// a stack with no protected route adopts freely and the dialog simply states the realm it is joining.
/// </para>
/// <para>
/// <b>One write, and therefore atomic without a wrapper.</b> The template link, the env rows and the
/// route are a single <c>SaveChangesAsync</c>, which EF executes in one transaction: a route insert
/// losing the unique-domain index rolls the adoption back with it. The explicit
/// <c>BeginTransactionAsync</c> that <c>templates.setTenantsRelease</c> (invariant 16) and
/// <see cref="TenantProvisioningService"/> need is for writers whose work spans <em>two</em> statements;
/// adding a second statement here means adding the wrapper with it. The proxy work is deliberately
/// outside: it is post-commit, best-effort and reads the committed row set, exactly as
/// <c>proxy.createRoute</c> sequences it.
/// </para>
/// <para>
/// Refusals are <c>Conflict</c> rather than <c>Validation</c>: <c>templates.addTenant</c> projects its
/// collisions as validation failures only because that is the shape it has always returned, and this
/// method has no such history. The messages follow its shape — they name the thing that is in the way.
/// </para>
/// </remarks>
[Handler("templates.adoptStack")]
public sealed class AdoptStack(
    WatchtowerDbContext db, IProxyProvider proxy, AuditLog audit, ICurrentUser currentUser)
    : IHandler<AdoptStack.Command, Result<AdoptStack.Response>> {
    /// <summary>Audit action for an adoption (docs/products/design.md §Audit, category <c>stacks</c>).</summary>
    public const string AuditAction = "tenant.adopt";

    /// <param name="TemplateId">The tenancy setup the stack joins.</param>
    /// <param name="StackId">
    /// The stack to adopt. Must be standalone and must already run the template's product — adoption
    /// never re-points a stack at another codebase.
    /// </param>
    /// <param name="Slug">The tenant slug it takes; normalized and validated exactly as provisioning does.</param>
    public sealed record Command(int TemplateId, int StackId, string Slug);

    /// <param name="Tenant">
    /// The adopted stack as a roster row. Its <c>domain</c> is the stack's <em>primary</em> domain, which
    /// is <paramref name="Domain"/> only when the stack had no primary route before — the roster reads
    /// the same rule, so the two cannot disagree.
    /// </param>
    /// <param name="Domain">The domain of the managed route this call created.</param>
    /// <param name="EnvKeysAdded">
    /// Base env keys copied from the template because the stack did not define them, sorted. Empty when
    /// the stack already defined all of them — which is the common case for a stack that was configured
    /// by hand to match its siblings.
    /// </param>
    /// <param name="DomainIsPrimary">
    /// Whether the new route became the stack's canonical domain. False when it already had one, which is
    /// the "kept the customer's domain" case worth saying out loud.
    /// </param>
    public sealed record Response(
        TenantDto Tenant, string Domain, IReadOnlyList<string> EnvKeysAdded, bool DomainIsPrimary);

    public async ValueTask<Result<Response>> HandleAsync(Command command, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(command);

        // Same two slug gates as provisioning, in the same order and on the normalized value, so a slug
        // this refuses is a slug templates.addTenant would have refused too.
        var normalized = TenancyMapping.NormalizeSlug(command.Slug);
        if (normalized is null) {
            return AppError.Validation(
                "Slug must start with a letter or digit and contain only lowercase letters, digits, and hyphens.");
        }
        if (TenantProvisioningService.ReservedSlugs.Contains(normalized))
            return AppError.Validation($"Slug '{normalized}' is reserved.");

        var template = await db.StackTemplates
            .Include(t => t.BaseEnvVars)
            .Include(t => t.Product)
            // Named in the realm refusal below, which is the whole content of that message.
            .Include(t => t.Realm)
            .FirstOrDefaultAsync(t => t.Id == command.TemplateId, ct);
        if (template is null)
            return AppError.NotFound($"Template {command.TemplateId} not found");

        var stack = await db.Stacks
            .Include(s => s.Product)
            // Loaded although a standalone stack has none: the refusal below names the setup a stack is
            // already in, and that name is the whole content of the message.
            .Include(s => s.Template)
            .Include(s => s.EnvVars)
            // Projected straight into the response's roster row, the two includes invariant 6 asks every
            // StackDto producer for.
            .Include(s => s.PinnedRelease)
            .Include(s => s.LastDeployedRelease)
            .FirstOrDefaultAsync(s => s.Id == command.StackId, ct);
        if (stack is null)
            return AppError.NotFound($"Stack {command.StackId} not found");

        if (stack.TemplateId is not null) {
            return AppError.Conflict(
                $"Stack '{stack.Name}' is already the tenant '{stack.TenantSlug}' of tenancy setup "
                + $"'{stack.Template!.Name}'. A stack belongs to one setup at a time.");
        }
        // The UpdateTemplate product-change refusal, one rung down: moving a template's product is
        // refused because every tenant would deploy a different codebase, and adopting across products
        // would do exactly that to this one stack — at its next deploy, silently.
        if (stack.ProductId != template.ProductId) {
            return AppError.Conflict(
                $"Stack '{stack.Name}' runs product '{stack.Product!.Name}' and '{template.Name}' runs "
                + $"'{template.Product!.Name}'. Adoption never re-points a stack at another codebase.");
        }

        // Refused rather than performed: see the class remarks. Only asked for a non-system realm,
        // because a standalone stack's routes are already in the system realm — adopting into a
        // system-realm setup moves nobody. `Public` routes are excluded because their admission does not
        // consult a realm at all, which is what keeps the common case (an unprotected stack joining a
        // customer-facing setup) a one-click operation.
        if (template.RealmId != Realm.SystemRealmId) {
            var protectedDomains = await db.Routes.AsNoTracking()
                .Where(r => r.StackId == stack.Id && r.AccessMode != AccessMode.Public)
                .OrderBy(r => r.Domain)
                .Select(r => r.Domain)
                .ToListAsync(ct);
            if (protectedDomains.Count > 0) {
                var realmName = template.Realm?.Name ?? $"realm {template.RealmId}";
                return AppError.Conflict(
                    $"Stack '{stack.Name}' has {protectedDomains.Count} protected domain(s) — "
                    + $"{string.Join(", ", protectedDomains)} — and '{template.Name}' serves the "
                    + $"'{realmName}' realm, so adopting it would change who is admitted to them. Make "
                    + "them public or remove their protection first; re-protect them after adopting, "
                    + $"deliberately, in the '{realmName}' realm — adoption must not move who is admitted "
                    + "as a side effect.");
            }
        }

        // The (template_id, tenant_slug) unique index in words. Naming the holder matters more here than
        // on provisioning: the caller is looking at a roster and needs to know which row it collided with.
        var slugHolder = await db.Stacks.AsNoTracking()
            .Where(s => s.TemplateId == template.Id && s.TenantSlug == normalized)
            .Select(s => s.Name)
            .FirstOrDefaultAsync(ct);
        if (slugHolder is not null) {
            return AppError.Conflict(
                $"Tenant '{normalized}' already exists in '{template.Name}' — it is stack '{slugHolder}'.");
        }

        // Domains are globally unique, and a rendered domain that is taken is refused rather than moved:
        // re-pointing a live hostname at another stack is not something an adoption should do quietly.
        var domain = TenancyMapping.RenderDomain(template.DomainPattern, normalized);
        var occupant = await db.Routes.AsNoTracking()
            .Where(r => r.Domain == domain)
            .Select(r => new { r.StackId, StackName = r.Stack!.Name })
            .FirstOrDefaultAsync(ct);
        if (occupant is not null) {
            return AppError.Conflict(occupant.StackId is null
                ? $"Domain '{domain}' is already routed to Watchtower itself."
                : $"Domain '{domain}' is already routed to stack '{occupant.StackName}'.");
        }

        // Never steal primary. A stack that already has a canonical domain keeps it; the new managed
        // subdomain becomes an additional way in.
        var primaryDomain = await db.Routes.AsNoTracking()
            .Where(r => r.StackId == stack.Id && r.IsPrimary)
            .Select(r => r.Domain)
            .FirstOrDefaultAsync(ct);
        var isPrimary = primaryDomain is null;

        // Only-missing-keys: the stack is running with its environment, so its values win by key.
        var stackKeys = stack.EnvVars.Select(v => v.Key).ToHashSet(StringComparer.Ordinal);
        var missing = template.BaseEnvVars
            .Where(v => !stackKeys.Contains(v.Key))
            .OrderBy(v => v.Key, StringComparer.Ordinal)
            .ToList();

        // Read before the link is written, so it is what the stack deploys *today*.
        var effectiveBranch = ProductSourceResolver.Resolve(stack).Branch;
        var inheritedAfterAdoption = ProductSourceResolver.InheritedBranch(template, stack.Product!);

        try {
            stack.TemplateId = template.Id;
            stack.TenantSlug = normalized;
            stack.BranchOverride = ProductSourceResolver.OverrideFor(effectiveBranch, inheritedAfterAdoption);

            foreach (var v in missing)
                db.StackEnvVars.Add(new StackEnvVar { StackId = stack.Id, Key = v.Key, Value = v.Value });

            db.Routes.Add(new Route {
                StackId = stack.Id,
                Domain = domain,
                ServiceName = template.TargetServiceName,
                ContainerPort = template.TargetPort,
                TlsEnabled = true,
                IsPrimary = isPrimary,
                Kind = DomainKind.Managed,
                Status = RouteStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        } catch (DbUpdateException ex) when (IsUniqueViolation(ex)) {
            // A concurrent write took the slug or the domain between the checks above and this one. The
            // batch is one transaction, so nothing reached the *database* — including the template link.
            // The change tracker still holds the mutated stack, which is harmless because a handler scope
            // is per dispatch and nothing else saves on it before it is disposed; a future caller that
            // continued using this context after the refusal would have to detach it.
            //
            // Filtered on the unique violation rather than catching every DbUpdateException, so a
            // DbUpdateConcurrencyException — Stack carries xmin, and this write mutates a loaded row —
            // escapes to the pipeline that answers "someone else changed this" instead of being reported
            // as a slug collision that did not happen.
            return AppError.Conflict(
                $"Stack '{stack.Name}' could not be adopted: the slug or the domain was taken concurrently.");
        }

        // Past the commit point, and best-effort exactly as proxy.createRoute sequences it: the target
        // container joins the edge network and the generated configuration is regenerated for the new
        // hostname. No deploy — nothing about what the stack runs changed.
        await proxy.ConnectStackAsync(stack.Id, ct);
        await proxy.ApplyAsync(ct);

        var envKeys = missing.Select(v => v.Key).ToList();
        await audit.RecordAsync(
            StackLifecycle.AuditCategory, AuditAction, stack.Name,
            $"adopted into '{template.Name}' as tenant '{normalized}'; route {domain} created"
            + (isPrimary ? " (primary)" : " (secondary — the stack kept its own primary domain)")
            + $"; {envKeys.Count} env var(s) added",
            actor: await audit.ActorAsync(currentUser, ct), ct: ct);

        return new Response(
            new TenantDto(
                stack.Id, normalized, stack.Name, primaryDomain ?? domain,
                stack.LastDeployStatus?.ToString().ToLowerInvariant(), stack.LastDeployedAt,
                stack.PinnedReleaseId is null ? TenancyMapping.TrackingLatest : TenancyMapping.TrackingPinned,
                TenancyMapping.ReleaseRef(stack.PinnedRelease),
                TenancyMapping.ReleaseRef(stack.LastDeployedRelease)),
            domain,
            envKeys,
            isPrimary);
    }

    /// <summary>A write that lost a race on a unique index, as opposed to any other write failure.</summary>
    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
