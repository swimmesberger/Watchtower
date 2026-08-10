using System.Text.Json.Serialization;
using Watchtower.Application.Services;

namespace Watchtower.Api.Endpoints;

/// <summary>
/// The public **App API** (<c>/api/app/*</c>): a token-authenticated REST surface that applications
/// deployed by Watchtower call to query <em>their own</em> deployment status, deployed version and
/// logs.
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint authenticates with <c>Authorization: Bearer &lt;token&gt;</c>, where the token is the
/// one Watchtower injected into the stack's environment as <c>WATCHTOWER_APP_TOKEN</c> at deploy time.
/// A missing, malformed or unknown token yields 401; a token belonging to a stack whose App API has
/// been switched off yields 403.
/// </para>
/// <para>
/// Container ids are never accepted from the caller — every Docker lookup is resolved server-side
/// from the authenticated stack's compose project label, so a stack can only ever observe itself.
/// Responses deliberately exclude deploy output, environment variable values and credentials.
/// </para>
/// <para>
/// One endpoint is not about the caller's own state: <c>/tenants/accessible</c> names sibling tenants.
/// It is not an exception to the rule above so much as a different question — it answers on behalf of a
/// visitor the caller proves with their forwarded identity assertion, about apps that visitor could reach
/// by typing the domain, and it carries nothing operational about those stacks.
/// </para>
/// <para>
/// Per ADR-0003 these are plain minimal-API routes rather than JSON-RPC handlers: they are externally
/// facing with their own auth semantics, and one of them is a stream.
/// </para>
/// </remarks>
public static class AppApiEndpoints {
    /// <summary>
    /// The user-scoped tenant switcher feed. Not "the caller's own state" like the rest of this surface,
    /// which is why it carries a second credential — see <see cref="MapAccessibleTenants"/>.
    /// </summary>
    private const string AccessibleTenantsPath = "/api/app/tenants/accessible";

    /// <summary>Maps every <c>/api/app/*</c> route onto the application.</summary>
    /// <param name="app">The web application to map onto.</param>
    /// <param name="authEnabled">
    /// Whether central authentication is configured. With it off no identity assertion exists, so the
    /// tenant-switcher endpoint answers 404 instead of asking for one.
    /// </param>
    /// <returns>The same application, for chaining.</returns>
    public static WebApplication MapAppApiEndpoints(this WebApplication app, bool authEnabled) {
        MapSelf(app);
        MapStatus(app);
        MapDeployments(app);
        MapVersion(app);
        MapLogs(app);
        MapAccessibleTenants(app, authEnabled);
        return app;
    }

    /// <summary>Identity of the calling stack.</summary>
    private static void MapSelf(WebApplication app) =>
        app.MapGet("/api/app/self", async (HttpRequest request, AppApiService api, CancellationToken ct) => {
            var (failure, caller) = await AuthenticateAsync(request, api, ct);
            return failure ?? Results.Json(AppApiService.GetSelf(caller!));
        });

    /// <summary>Last-deploy metadata, the in-flight deploy if any, and live per-service container state.</summary>
    private static void MapStatus(WebApplication app) =>
        app.MapGet("/api/app/status", async (HttpRequest request, AppApiService api, CancellationToken ct) => {
            var (failure, caller) = await AuthenticateAsync(request, api, ct);
            if (failure is not null) return failure;
            try {
                return Results.Json(await api.GetStatusAsync(caller!, ct));
            } catch (HttpRequestException) {
                return DockerUnavailable();
            }
        });

    /// <summary>
    /// Recent deploy events, newest first. <c>limit</c> defaults to 20 and is capped at 100. The
    /// captured command output is never included — it is produced with credentials in scope.
    /// </summary>
    private static void MapDeployments(WebApplication app) =>
        app.MapGet("/api/app/deployments", async (
            int? limit, HttpRequest request, AppApiService api, CancellationToken ct) => {
            var (failure, caller) = await AuthenticateAsync(request, api, ct);
            return failure ?? Results.Json(await api.GetDeploymentsAsync(caller!, limit, ct));
        });

    /// <summary>Deployed commit plus the live image identity of every service container.</summary>
    private static void MapVersion(WebApplication app) =>
        app.MapGet("/api/app/version", async (HttpRequest request, AppApiService api, CancellationToken ct) => {
            var (failure, caller) = await AuthenticateAsync(request, api, ct);
            if (failure is not null) return failure;
            try {
                return Results.Json(await api.GetVersionAsync(caller!, ct));
            } catch (HttpRequestException) {
                return DockerUnavailable();
            }
        });

    /// <summary>
    /// Streams the calling stack's container logs as Server-Sent Events, using the same SSE mechanics
    /// as the operator-facing <c>/api/containers/{id}/logs</c> stream.
    /// </summary>
    /// <remarks>
    /// The target containers are resolved from the authenticated stack's compose project label; the
    /// stream contract itself — ambiguity handling, replica prefixes, error frames, the terminal
    /// <c>done</c> — lives in <see cref="SseLogStreaming"/>, shared with the management API.
    /// </remarks>
    private static void MapLogs(WebApplication app) =>
        app.MapGet("/api/app/logs", async (
            string? service, int? tail, bool? follow,
            HttpRequest request, HttpResponse response,
            AppApiService api, DockerEngineClient docker, CancellationToken ct) => {
            var (failure, caller) = await AuthenticateAsync(request, api, ct);
            if (failure is not null) return failure;

            IReadOnlyList<DockerContainerInfo> containers;
            try {
                containers = await api.ListMemberContainersAsync(caller!, service, ct);
            } catch (HttpRequestException) {
                // Still before any body was written, so a normal status response is possible.
                return DockerUnavailable();
            }

            return await SseLogStreaming.ServeLogsAsync(response, docker, containers, service, tail, follow, ct);
        });

    /// <summary>
    /// The sibling tenants of the caller's own template that the <em>visiting user</em> may switch to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two credentials, and both are load-bearing. The stack authenticates as usual with its App API token,
    /// and additionally forwards the <c>X-Watchtower-Jwt</c> assertion from the request it is currently
    /// serving. The assertion is what names the user — no endpoint here accepts a bare user id — and it is
    /// only accepted when its <c>aud</c> is one of <em>this</em> stack's domains, so an app can only ask
    /// about somebody actually visiting it.
    /// </para>
    /// <para>
    /// A stack that is not a tenant of a template gets 404: it has nothing to switch between, and it already
    /// knows its own nature, so saying so discloses nothing. Every assertion failure gets the same 401.
    /// </para>
    /// </remarks>
    private static void MapAccessibleTenants(WebApplication app, bool authEnabled) {
        // Mapped-but-404 rather than left unmapped: an unmapped /api/app/* path falls through to the SPA
        // fallback and answers index.html with a 200, which is a baffling reply to an API call. Same shape
        // as the forward-auth surface takes (see WatchtowerAccessEndpoints).
        if (!authEnabled) {
            app.MapGet(AccessibleTenantsPath, () => Results.NotFound());
            return;
        }

        app.MapGet(AccessibleTenantsPath, async (
            HttpRequest request, AppApiService api, TenantDiscoveryService tenants, CancellationToken ct) => {
            var (failure, caller) = await AuthenticateAsync(request, api, ct);
            if (failure is not null) return failure;

            var templateId = await tenants.ResolveTenantTemplateIdAsync(caller!.StackId, ct);
            if (templateId is null)
                return Results.Json(new AppApiErrorDto("This stack is not a tenant of a template."),
                    statusCode: StatusCodes.Status404NotFound);

            var userId = await tenants.ResolveAssertionSubjectAsync(
                caller.StackId, request.Headers[RouteAccessPolicy.JwtHeaderName], ct);
            if (userId is null) return InvalidUserAssertion();

            var accessible = await tenants.ListAccessibleTenantsAsync(templateId.Value, userId.Value, ct);
            return Results.Json(new AppAccessibleTenantsDto(accessible
                .Select(t => new AppAccessibleTenantDto(t.Slug, t.Domain, t.StackId == caller.StackId))
                .ToList()));
        });
    }

    /// <summary>
    /// 401 for a request whose forwarded identity assertion is missing, unverifiable, minted for another
    /// app, or names an account that is gone or disabled.
    /// </summary>
    /// <remarks>
    /// Deliberately one message for all of those, shared by both public surfaces: an answer that
    /// distinguished them would tell a caller which check it tripped, and "wrong audience" versus "no such
    /// account" is exactly the difference an enumeration attempt is looking for.
    /// </remarks>
    internal static IResult InvalidUserAssertion() =>
        Results.Json(new AppApiErrorDto("Missing or invalid user assertion."),
            statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>503 response used when the Docker daemon cannot be reached.</summary>
    /// <remarks>Shared with the management API, which reads live state through the same client.</remarks>
    internal static IResult DockerUnavailable() =>
        Results.Json(new AppApiErrorDto("The Docker daemon is currently unreachable."),
            statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// Authenticates the request, translating a rejection into the response to return.
    /// </summary>
    /// <param name="request">Incoming request; only its <c>Authorization</c> header is read.</param>
    /// <param name="api">The App API service performing the lookup.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// Either a non-null failure result (401/403) to return verbatim, or a non-null caller to serve.
    /// </returns>
    private static async Task<(IResult? Failure, AppApiCaller? Caller)> AuthenticateAsync(
        HttpRequest request, AppApiService api, CancellationToken ct) {
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
}

/// <summary>
/// Source-generated JSON metadata for the host's plain-HTTP response bodies (the App API DTOs and the
/// webhook result). Registered into the minimal-API serializer chain in <c>Program.cs</c> so these
/// responses never depend on reflection-based serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSelfDto))]
[JsonSerializable(typeof(AppStatusDto))]
[JsonSerializable(typeof(AppDeploymentsDto))]
[JsonSerializable(typeof(AppVersionDto))]
[JsonSerializable(typeof(AppAccessibleTenantsDto))]
[JsonSerializable(typeof(AppApiErrorDto))]
[JsonSerializable(typeof(MgmtTemplatesDto))]
[JsonSerializable(typeof(MgmtTenantsDto))]
[JsonSerializable(typeof(MgmtAccessibleTenantsDto))]
[JsonSerializable(typeof(MgmtTenantCreatedDto))]
[JsonSerializable(typeof(MgmtTenantStatusDto))]
[JsonSerializable(typeof(MgmtDeployAcceptedDto))]
[JsonSerializable(typeof(MgmtTenantDeletedDto))]
[JsonSerializable(typeof(MgmtCreateTenantRequest))]
[JsonSerializable(typeof(WatchtowerHttpEndpoints.WebhookDeployResult))]
[JsonSerializable(typeof(WatchtowerAccessEndpoints.AppsResponse))]
public sealed partial class WatchtowerHttpJsonContext : JsonSerializerContext;
