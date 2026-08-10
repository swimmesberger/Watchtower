using System.Text.Json;
using Watchtower.Application.Services;

namespace Watchtower.Api.Endpoints;

/// <summary>
/// The public **management API** (<c>/api/mgmt/*</c>): the surface a vendor's central management UI —
/// itself deployed as a Watchtower stack — uses to run the tenants of a <c>StackTemplate</c> it has been
/// granted: list, provision, inspect, redeploy, stream logs and deprovision.
/// </summary>
/// <remarks>
/// <para>
/// The credential is the caller stack's own App API token (<c>WATCHTOWER_APP_TOKEN</c>), presented as
/// <c>Authorization: Bearer wtapp_…</c>. The token alone grants nothing here: authorization is an
/// operator-managed <c>TemplateManagementGrant</c> linking that stack to that template, so a vendor's UI
/// never holds operator credentials and an operator can withdraw management by deleting one row.
/// </para>
/// <para>
/// A caller with no grant on a template gets 404, not 403 — the same answer a template that does not
/// exist gets. The surface never confirms the existence of anything it will not serve. Tenants are
/// likewise always looked up <em>within</em> the granted template, so a slug belonging to another
/// template is simply not found rather than reachable.
/// </para>
/// <para>
/// Nothing here returns environment values, deploy output, App API tokens or credentials, and every
/// Docker lookup is scoped by the resolved tenant's compose project label rather than by anything in the
/// request — the same rules the App API already enforces, reached through the same service.
/// </para>
/// <para>
/// Per ADR-0003 these are plain minimal-API routes rather than JSON-RPC handlers: they are externally
/// facing with their own auth semantics, and one of them is a stream.
/// </para>
/// </remarks>
public static class MgmtApiEndpoints {
    /// <summary>
    /// The user-scoped tenant feed. The literal <c>accessible</c> segment outranks the <c>{slug}</c>
    /// parameter when routing, which would shadow a tenant of that name on the tenant-status route — so the
    /// word is reserved at provisioning (<see cref="TenantProvisioningService.ReservedSlugs"/>) and no such
    /// tenant can be created. Any literal segment added here later has to join that set.
    /// </summary>
    private const string AccessibleTenantsPath = "/api/mgmt/templates/{templateId:int}/tenants/accessible";

    /// <summary>Maps every <c>/api/mgmt/*</c> route onto the application.</summary>
    /// <param name="app">The web application to map onto.</param>
    /// <param name="authEnabled">
    /// Whether central authentication is configured. With it off no identity assertion exists, so the
    /// user-filtered tenant listing answers 404 instead of asking for one.
    /// </param>
    /// <returns>The same application, for chaining.</returns>
    public static WebApplication MapMgmtApiEndpoints(this WebApplication app, bool authEnabled) {
        MapTemplates(app);
        MapTenants(app);
        MapAccessibleTenants(app, authEnabled);
        MapCreateTenant(app);
        MapTenantStatus(app);
        MapDeploy(app);
        MapDeployments(app);
        MapTenantLogs(app);
        MapDeleteTenant(app);
        return app;
    }

    /// <summary>The templates this caller may manage, with its own capability on each.</summary>
    private static void MapTemplates(WebApplication app) =>
        app.MapGet("/api/mgmt/templates", async (
            HttpRequest request, MgmtApiService api, CancellationToken ct) => {
            var (failure, caller) = await AuthenticateAsync(request, api, ct);
            return failure ?? Results.Json(await api.ListTemplatesAsync(caller!.StackId, ct));
        });

    /// <summary>The tenants of one managed template, newest first.</summary>
    private static void MapTenants(WebApplication app) =>
        app.MapGet("/api/mgmt/templates/{templateId:int}/tenants", async (
            int templateId, HttpRequest request, MgmtApiService api, CancellationToken ct) => {
            var (failure, _, _) = await AuthorizeAsync(request, api, templateId, ct);
            return failure ?? Results.Json(await api.ListTenantsAsync(templateId, ct));
        });

    /// <summary>
    /// The managed template's tenants that the <em>visiting user</em> may enter — the same tenant-switcher
    /// feed the App API serves, for a vendor's management UI.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The grant gate runs first and unchanged: an ungranted template is the surface's uniform 404 before
    /// any assertion is looked at, so this endpoint cannot become the oracle for template ids the rest of
    /// the surface refuses to be. Only then is the forwarded <c>X-Watchtower-Jwt</c> considered, under
    /// exactly the App API's rules — its <c>aud</c> must be one of the <em>management</em> stack's own
    /// domains, and every failure is the same 401.
    /// </para>
    /// <para>
    /// The unfiltered <see cref="MapTenants"/> listing is untouched and remains the operational view; this
    /// one answers "which of these may the person looking at the screen open?".
    /// </para>
    /// </remarks>
    private static void MapAccessibleTenants(WebApplication app, bool authEnabled) {
        // Mapped-but-404 with auth off, for the same reason the App API does it: an unmapped path would be
        // answered by the SPA fallback with index.html and a 200.
        if (!authEnabled) {
            app.MapGet(AccessibleTenantsPath, () => Results.NotFound());
            return;
        }

        app.MapGet(AccessibleTenantsPath, async (
            int templateId, HttpRequest request,
            MgmtApiService api, TenantDiscoveryService tenants, CancellationToken ct) => {
            var (failure, caller, _) = await AuthorizeAsync(request, api, templateId, ct);
            if (failure is not null) return failure;

            var userId = await tenants.ResolveAssertionSubjectAsync(
                caller!.StackId, request.Headers[RouteAccessPolicy.JwtHeaderName], ct);
            if (userId is null) return AppApiEndpoints.InvalidUserAssertion();

            var accessible = await tenants.ListAccessibleTenantsAsync(templateId, userId.Value, ct);
            return Results.Json(new MgmtAccessibleTenantsDto(accessible
                .Select(t => new MgmtAccessibleTenantDto(t.Slug, t.Domain))
                .ToList()));
        });
    }

    /// <summary>
    /// Provisions a tenant: a stack of its own, the template's env vars with the request's overrides
    /// merged in, a route derived from the template's domain pattern, and an initial deploy.
    /// </summary>
    /// <remarks>
    /// The body is <c>{ "slug": "customer4", "env": { "KEY": "value" } }</c>, <c>env</c> optional. Slug
    /// and env-name validation answer 400; a slug, domain or stack name already taken answers 409.
    /// </remarks>
    private static void MapCreateTenant(WebApplication app) =>
        app.MapPost("/api/mgmt/templates/{templateId:int}/tenants", async (
            int templateId, HttpRequest request, MgmtApiService api, CancellationToken ct) => {
            var (failure, _, _) = await AuthorizeAsync(request, api, templateId, ct);
            if (failure is not null) return failure;

            MgmtCreateTenantRequest? body;
            try {
                body = await request.ReadFromJsonAsync<MgmtCreateTenantRequest>(ct);
            } catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException) {
                // Unparseable, wrong content type, or no JSON at all — a malformed request, not a
                // server fault. The reason is deliberately generic.
                return BadRequest("The request body must be a JSON object.");
            }

            var result = await api.CreateTenantAsync(templateId, body, ct);
            return result.Status switch {
                TenantProvisionStatus.Created => Results.Created(
                    $"/api/mgmt/templates/{templateId}/tenants/{result.Created!.Slug}", result.Created),
                TenantProvisionStatus.Conflict => Conflict(result.Error!),
                // The template was deleted between the grant check and the write; its grants cascaded
                // away with it, so answer as the grant check would have.
                TenantProvisionStatus.TemplateNotFound => TemplateNotFound(),
                _ => BadRequest(result.Error!),
            };
        });

    /// <summary>Deployment status plus live per-service container state for one tenant.</summary>
    private static void MapTenantStatus(WebApplication app) =>
        app.MapGet("/api/mgmt/templates/{templateId:int}/tenants/{slug}", async (
            int templateId, string slug, HttpRequest request, MgmtApiService api, CancellationToken ct) => {
            var (failure, tenant) = await ResolveTenantAsync(request, api, templateId, slug, ct);
            if (failure is not null) return failure;
            try {
                return Results.Json(await api.GetTenantStatusAsync(tenant!, ct));
            } catch (HttpRequestException) {
                return AppApiEndpoints.DockerUnavailable();
            }
        });

    /// <summary>Queues a redeploy of one tenant.</summary>
    private static void MapDeploy(WebApplication app) =>
        app.MapPost("/api/mgmt/templates/{templateId:int}/tenants/{slug}/deploy", async (
            int templateId, string slug, HttpRequest request, MgmtApiService api, CancellationToken ct) => {
            var (failure, tenant) = await ResolveTenantAsync(request, api, templateId, slug, ct);
            if (failure is not null) return failure;
            return Results.Json(api.EnqueueDeploy(tenant!), statusCode: StatusCodes.Status202Accepted);
        });

    /// <summary>
    /// Recent deploy events for one tenant, newest first. <c>limit</c> defaults to 20 and is capped at
    /// 100. The captured command output is never included — it is produced with credentials in scope.
    /// </summary>
    private static void MapDeployments(WebApplication app) =>
        app.MapGet("/api/mgmt/templates/{templateId:int}/tenants/{slug}/deployments", async (
            int templateId, string slug, int? limit,
            HttpRequest request, MgmtApiService api, CancellationToken ct) => {
            var (failure, tenant) = await ResolveTenantAsync(request, api, templateId, slug, ct);
            return failure ?? Results.Json(await api.GetDeploymentsAsync(tenant!, limit, ct));
        });

    /// <summary>
    /// Streams one tenant's container logs as Server-Sent Events, on exactly the App API's
    /// <c>/logs</c> contract (see <see cref="SseLogStreaming"/>).
    /// </summary>
    private static void MapTenantLogs(WebApplication app) =>
        app.MapGet("/api/mgmt/templates/{templateId:int}/tenants/{slug}/logs", async (
            int templateId, string slug, string? service, int? tail, bool? follow,
            HttpRequest request, HttpResponse response,
            MgmtApiService api, DockerEngineClient docker, CancellationToken ct) => {
            var (failure, tenant) = await ResolveTenantAsync(request, api, templateId, slug, ct);
            if (failure is not null) return failure;

            IReadOnlyList<DockerContainerInfo> containers;
            try {
                containers = await api.ListTenantContainersAsync(tenant!, service, ct);
            } catch (HttpRequestException) {
                // Still before any body was written, so a normal status response is possible.
                return AppApiEndpoints.DockerUnavailable();
            }

            return await SseLogStreaming.ServeLogsAsync(response, docker, containers, service, tail, follow, ct);
        });

    /// <summary>
    /// Deprovisions one tenant: containers down, stack row deleted, proxy reloaded. Requires
    /// <c>AllowDelete</c> on the grant; <c>?volumes=true</c> additionally destroys the tenant's data.
    /// </summary>
    private static void MapDeleteTenant(WebApplication app) =>
        app.MapDelete("/api/mgmt/templates/{templateId:int}/tenants/{slug}", async (
            int templateId, string slug, bool? volumes,
            HttpRequest request, MgmtApiService api, CancellationToken ct) => {
            var (failure, _, grant) = await AuthorizeAsync(request, api, templateId, ct);
            if (failure is not null) return failure;

            // Checked before the tenant is even looked up: a grant without delete rights learns nothing
            // from this endpoint, not even whether the slug exists.
            if (!grant!.AllowDelete)
                return Results.Json(new AppApiErrorDto("Delete is not permitted for this grant."),
                    statusCode: StatusCodes.Status403Forbidden);

            var result = await api.DeleteTenantAsync(templateId, slug, volumes ?? false, ct);
            return result.Status switch {
                // The normalized slug, not the one in the URL: it is the identifier that was stored.
                TenantTeardownStatus.Removed => Results.Json(new MgmtTenantDeletedDto(result.Slug!, true)),
                TenantTeardownStatus.TenantNotFound => TenantNotFound(),
                TenantTeardownStatus.DeployActive => Conflict(result.Error!),
                // Compose could not stop the containers, so nothing was deleted. The command output
                // stays in Watchtower's log rather than in this body.
                _ => Results.Json(new AppApiErrorDto(result.Error!),
                    statusCode: StatusCodes.Status500InternalServerError),
            };
        });

    // ── Request pipeline ─────────────────────────────────────────────────────

    /// <summary>
    /// Authenticates the request, translating a rejection into the response to return.
    /// </summary>
    /// <remarks>
    /// Identical semantics to the App API, by construction: the same token, the same lookup, and the
    /// same <c>AppApiEnabled</c> kill switch closing both surfaces at once.
    /// </remarks>
    /// <param name="request">Incoming request; only its <c>Authorization</c> header is read.</param>
    /// <param name="api">The management API service performing the lookup.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Either a non-null failure result (401/403) to return verbatim, or a non-null caller.</returns>
    private static async Task<(IResult? Failure, AppApiCaller? Caller)> AuthenticateAsync(
        HttpRequest request, MgmtApiService api, CancellationToken ct) {
        var result = await api.AuthenticateAsync(request.Headers.Authorization.ToString(), ct);
        return result.Status switch {
            AppApiAuthStatus.Ok => (null, result.Caller),
            AppApiAuthStatus.Forbidden => (
                Results.Json(new AppApiErrorDto("The App API is disabled for this stack."),
                    statusCode: StatusCodes.Status403Forbidden), null),
            _ => (
                Results.Json(new AppApiErrorDto("Missing or invalid App API token."),
                    statusCode: StatusCodes.Status401Unauthorized), null),
        };
    }

    /// <summary>
    /// Authenticates the caller and resolves its grant on <paramref name="templateId"/>.
    /// </summary>
    /// <remarks>
    /// The one authorization gate of this surface. "No grant" and "no such template" are deliberately
    /// the same 404 with the same body: distinguishing them would turn the endpoint into an oracle for
    /// which template ids exist.
    /// </remarks>
    /// <param name="request">Incoming request.</param>
    /// <param name="api">The management API service.</param>
    /// <param name="templateId">Template the request is addressed to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A failure result (401/403/404), or the authenticated caller and its grant.</returns>
    private static async Task<(IResult? Failure, AppApiCaller? Caller, MgmtGrant? Grant)> AuthorizeAsync(
        HttpRequest request, MgmtApiService api, int templateId, CancellationToken ct) {
        var (failure, caller) = await AuthenticateAsync(request, api, ct);
        if (failure is not null) return (failure, null, null);

        var grant = await api.ResolveGrantAsync(caller!.StackId, templateId, ct);
        return grant is null ? (TemplateNotFound(), null, null) : (null, caller, grant);
    }

    /// <summary>Authenticates, checks the grant, and resolves the tenant within that template.</summary>
    /// <param name="request">Incoming request.</param>
    /// <param name="api">The management API service.</param>
    /// <param name="templateId">Template the request is addressed to.</param>
    /// <param name="slug">Tenant slug from the request path.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A failure result (401/403/404), or the resolved tenant.</returns>
    private static async Task<(IResult? Failure, MgmtTenant? Tenant)> ResolveTenantAsync(
        HttpRequest request, MgmtApiService api, int templateId, string slug, CancellationToken ct) {
        var (failure, _, _) = await AuthorizeAsync(request, api, templateId, ct);
        if (failure is not null) return (failure, null);

        var tenant = await api.ResolveTenantAsync(templateId, slug, ct);
        return tenant is null ? (TenantNotFound(), null) : (null, tenant);
    }

    // ── Shared responses ─────────────────────────────────────────────────────

    /// <summary>404 for a template that does not exist <em>or</em> that this caller has no grant on.</summary>
    private static IResult TemplateNotFound() =>
        Results.Json(new AppApiErrorDto("Template not found."), statusCode: StatusCodes.Status404NotFound);

    /// <summary>404 for a slug that names no tenant of the granted template.</summary>
    private static IResult TenantNotFound() =>
        Results.Json(new AppApiErrorDto("Tenant not found."), statusCode: StatusCodes.Status404NotFound);

    private static IResult BadRequest(string error) =>
        Results.Json(new AppApiErrorDto(error), statusCode: StatusCodes.Status400BadRequest);

    private static IResult Conflict(string error) =>
        Results.Json(new AppApiErrorDto(error), statusCode: StatusCodes.Status409Conflict);
}
