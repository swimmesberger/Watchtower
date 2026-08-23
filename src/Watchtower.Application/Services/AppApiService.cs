using Microsoft.EntityFrameworkCore;
using Watchtower.Application.Entities;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

// ── Wire DTOs ────────────────────────────────────────────────────────────────
// Everything a stack is allowed to learn about itself. Deliberately absent everywhere:
// deploy output (produced with credentials in scope), environment variable values, credentials,
// and anything belonging to another stack.

/// <summary>Identity of the calling stack (<c>GET /api/app/self</c>).</summary>
/// <param name="StackId">Watchtower's numeric id for this stack.</param>
/// <param name="Name">Operator-visible stack name.</param>
/// <param name="TenantSlug">Tenant identifier when the stack is a template instance; otherwise null.</param>
public sealed record AppSelfDto(int StackId, string Name, string? TenantSlug);

/// <summary>Live state of one compose service container.</summary>
/// <param name="Service">Value of the <c>com.docker.compose.service</c> label.</param>
/// <param name="ContainerId">Docker container id — scoped to the caller's own stack.</param>
/// <param name="State">Docker's coarse state, e.g. <c>running</c>, <c>exited</c>.</param>
/// <param name="Status">Docker's human status line, e.g. <c>Up 3 hours</c>.</param>
/// <param name="Image">Image reference the container was started from.</param>
public sealed record AppServiceStatusDto(
    string Service, string? ContainerId, string State, string Status, string Image);

/// <summary>The deploy currently queued or running for this stack.</summary>
/// <param name="Id">Deploy event id.</param>
/// <param name="Status">One of <c>queued</c> or <c>running</c>.</param>
/// <param name="StartedAt">When the deploy event was created.</param>
public sealed record AppActiveDeployDto(int Id, string Status, DateTimeOffset StartedAt);

/// <summary>Deployment and runtime status of the calling stack (<c>GET /api/app/status</c>).</summary>
/// <param name="LastDeployStatus">Status of the last deploy: <c>queued|running|success|failed</c>, or null before the first.</param>
/// <param name="LastDeployedAt">When the last deploy reached a terminal state.</param>
/// <param name="LastDeployedCommit">Commit SHA checked out by the last successful deploy.</param>
/// <param name="ActiveDeploy">The in-flight deploy, or null when nothing is queued or running.</param>
/// <param name="Services">Live container state per compose service.</param>
public sealed record AppStatusDto(
    string? LastDeployStatus,
    DateTimeOffset? LastDeployedAt,
    string? LastDeployedCommit,
    AppActiveDeployDto? ActiveDeploy,
    IReadOnlyList<AppServiceStatusDto> Services);

/// <summary>One historical deploy. Never carries the deploy's captured command output.</summary>
/// <param name="Id">Deploy event id.</param>
/// <param name="Status">One of <c>queued|running|success|failed</c>.</param>
/// <param name="TriggeredBy">What started it, e.g. <c>manual</c>, <c>webhook</c>, <c>auto-update</c>.</param>
/// <param name="StartedAt">When the deploy event was created.</param>
/// <param name="FinishedAt">When it reached a terminal state; null while in flight.</param>
public sealed record AppDeploymentDto(
    int Id, string Status, string TriggeredBy, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

/// <summary>Recent deploy history, newest first (<c>GET /api/app/deployments</c>).</summary>
/// <param name="Deployments">The page of deploy events.</param>
public sealed record AppDeploymentsDto(IReadOnlyList<AppDeploymentDto> Deployments);

/// <summary>Deployed image identity for one compose service.</summary>
/// <param name="Service">Value of the <c>com.docker.compose.service</c> label.</param>
/// <param name="Image">Image reference the container was started from.</param>
/// <param name="ImageId">Local sha256 image id, when the image could be inspected.</param>
/// <param name="Digest">Registry content digest (<c>sha256:…</c>), when the image carries a repo digest.</param>
public sealed record AppVersionServiceDto(string Service, string Image, string? ImageId, string? Digest);

/// <summary>What version of itself the stack is currently running (<c>GET /api/app/version</c>).</summary>
/// <param name="Commit">Commit SHA of the last successful deploy; null before the first.</param>
/// <param name="DeployedAt">When the last deploy reached a terminal state.</param>
/// <param name="Services">Per-service image identity, read live from Docker.</param>
public sealed record AppVersionDto(
    string? Commit, DateTimeOffset? DeployedAt, IReadOnlyList<AppVersionServiceDto> Services);

/// <summary>
/// One tenant the visiting user may switch to (<c>GET /api/app/tenants/accessible</c>). Deliberately just
/// enough to render a switcher entry: this response is shown to end users.
/// </summary>
/// <param name="Slug">Tenant identifier within the template.</param>
/// <param name="Domain">Primary domain that tenant is served on.</param>
/// <param name="Current">True for exactly the stack answering the request.</param>
public sealed record AppAccessibleTenantDto(string Slug, string Domain, bool Current);

/// <summary>The sibling tenants of the caller's template the visiting user may enter, by slug ascending.</summary>
/// <param name="Tenants">The accessible tenants, including the caller itself when the user may enter it.</param>
public sealed record AppAccessibleTenantsDto(IReadOnlyList<AppAccessibleTenantDto> Tenants);

/// <summary>Error body returned by the App API for 400/401/403/404 responses.</summary>
/// <param name="Error">Human-readable message. Never contains tokens, credentials or deploy output.</param>
/// <param name="Services">Available compose service names, when the error is "ambiguous service".</param>
public sealed record AppApiErrorDto(string Error, IReadOnlyList<string>? Services = null);

// ── Authentication ───────────────────────────────────────────────────────────

/// <summary>Outcome of authenticating an <c>/api/app/*</c> request.</summary>
public enum AppApiAuthStatus {
    /// <summary>Token matched an enabled stack; the request may proceed.</summary>
    Ok,
    /// <summary>No token, a malformed token, or a token that matches no stack — 401.</summary>
    Unauthorized,
    /// <summary>Token matched a stack whose App API is switched off — 403.</summary>
    Forbidden,
}

/// <summary>
/// The authenticated stack. Every Docker lookup is resolved from
/// <paramref name="ComposeProjectName"/>; container ids are never accepted from the caller.
/// </summary>
/// <param name="StackId">Watchtower's numeric id for the stack.</param>
/// <param name="Name">Operator-visible stack name.</param>
/// <param name="ComposeProjectName">Value of the stack's <c>com.docker.compose.project</c> label.</param>
/// <param name="TenantSlug">Tenant identifier when the stack is a template instance; otherwise null.</param>
/// <param name="LastDeployStatus">Status of the last deploy.</param>
/// <param name="LastDeployedAt">When the last deploy reached a terminal state.</param>
/// <param name="LastDeployedCommit">Commit SHA of the last successful deploy.</param>
public sealed record AppApiCaller(
    int StackId,
    string Name,
    string ComposeProjectName,
    string? TenantSlug,
    DeployStatus? LastDeployStatus,
    DateTimeOffset? LastDeployedAt,
    string? LastDeployedCommit);

/// <summary>Result of <see cref="AppApiService.AuthenticateAsync"/>.</summary>
/// <param name="Status">Whether the request is authorized, and why not when it isn't.</param>
/// <param name="Caller">The authenticated stack; null unless <paramref name="Status"/> is <see cref="AppApiAuthStatus.Ok"/>.</param>
public sealed record AppApiAuthResult(AppApiAuthStatus Status, AppApiCaller? Caller);

// ── Service ──────────────────────────────────────────────────────────────────

/// <summary>
/// Backs the public App API (<c>/api/app/*</c>): token authentication, server-side resolution of the
/// caller's own containers, and the status/deployments/version read models. The host endpoints stay
/// thin translation layers over this service.
/// </summary>
/// <remarks>
/// Two invariants hold throughout: a caller may only ever observe the stack its token belongs to, and
/// container identities are resolved from the authenticated stack's compose project label rather than
/// from anything in the request.
/// </remarks>
/// <param name="db">Scoped Watchtower database context.</param>
/// <param name="docker">Docker Engine API client used for live container/image lookups.</param>
public sealed class AppApiService(WatchtowerDbContext db, DockerEngineClient docker) {
    /// <summary>Compose label identifying the project (stack) a container belongs to.</summary>
    public const string ProjectLabel = "com.docker.compose.project";

    /// <summary>Compose label identifying the service a container implements.</summary>
    public const string ServiceLabel = "com.docker.compose.service";

    /// <summary>Default number of deploy events returned by <see cref="GetDeploymentsAsync"/>.</summary>
    public const int DefaultDeploymentLimit = 20;

    /// <summary>Hard ceiling on the deploy-event page size.</summary>
    public const int MaxDeploymentLimit = 100;

    /// <summary>Default number of log lines replayed by the log stream.</summary>
    public const int DefaultLogTail = 100;

    /// <summary>Hard ceiling on replayed log lines, so one call can't ask Docker for unbounded history.</summary>
    public const int MaxLogTail = 5000;

    /// <summary>
    /// Authenticates an <c>/api/app/*</c> request from its raw <c>Authorization</c> header.
    /// </summary>
    /// <remarks>
    /// A missing, non-Bearer or wrong-shaped token is rejected before any database access. Otherwise
    /// the row is selected by an indexed SQL equality predicate on the token column — that predicate
    /// is what decides the match, and it is not a constant-time comparison. The subsequent
    /// <see cref="AppApiTokens.Verify"/> call is defense in depth on the already-selected row: it
    /// catches a mismatch the store might allow (e.g. an unexpected collation) without claiming to
    /// close a timing channel the database lookup itself leaves open. A matched but disabled stack
    /// yields <see cref="AppApiAuthStatus.Forbidden"/>.
    /// </remarks>
    /// <param name="authorizationHeader">Raw <c>Authorization</c> header value; may be null or empty.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The authentication outcome and, on success, the calling stack.</returns>
    public async Task<AppApiAuthResult> AuthenticateAsync(string? authorizationHeader, CancellationToken ct) {
        if (AppApiTokens.ExtractBearer(authorizationHeader) is not { } presented)
            return new AppApiAuthResult(AppApiAuthStatus.Unauthorized, Caller: null);

        var match = await db.Stacks.AsNoTracking()
            .Where(s => s.AppApiToken == presented)
            .Select(s => new {
                s.Id, s.Name, s.ComposeProjectName, s.TenantSlug, s.AppApiToken, s.AppApiEnabled,
                s.LastDeployStatus, s.LastDeployedAt, s.LastDeployedCommit,
            })
            .FirstOrDefaultAsync(ct);

        // Constant-time re-check: the indexed lookup narrows the row, this decides the match.
        if (match is null || !AppApiTokens.Verify(presented, match.AppApiToken))
            return new AppApiAuthResult(AppApiAuthStatus.Unauthorized, Caller: null);

        if (!match.AppApiEnabled)
            return new AppApiAuthResult(AppApiAuthStatus.Forbidden, Caller: null);

        return new AppApiAuthResult(AppApiAuthStatus.Ok, new AppApiCaller(
            match.Id, match.Name, match.ComposeProjectName, match.TenantSlug,
            match.LastDeployStatus, match.LastDeployedAt, match.LastDeployedCommit));
    }

    /// <summary>
    /// Returns the stack's token, generating and persisting one when it has none yet.
    /// </summary>
    /// <remarks>
    /// The write is a single conditional <c>UPDATE … WHERE app_api_token IS NULL</c>, so when a deploy
    /// and an operator opening the App API panel race, exactly one generated token is persisted and
    /// both callers observe it. The stack must exist; for a missing stack the freshly generated (and
    /// unpersisted) value is returned, so callers check existence first.
    /// </remarks>
    /// <param name="stackId">Stack to read or initialize.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stack's persisted App API token.</returns>
    public async Task<string> EnsureTokenAsync(int stackId, CancellationToken ct) {
        var current = await ReadTokenAsync(stackId, ct);
        if (!string.IsNullOrEmpty(current)) return current;

        var generated = AppApiTokens.Generate();
        await db.Stacks
            .Where(s => s.Id == stackId && s.AppApiToken == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AppApiToken, generated), ct);

        // Re-read rather than assume we won the race: a concurrent writer's token is the truth.
        return await ReadTokenAsync(stackId, ct) ?? generated;
    }

    /// <summary>
    /// Replaces the stack's token with a freshly generated one, invalidating the value currently
    /// baked into the running containers' environment until the next deploy.
    /// </summary>
    /// <param name="stackId">Stack to rotate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The newly persisted token.</returns>
    public async Task<string> RegenerateTokenAsync(int stackId, CancellationToken ct) {
        var generated = AppApiTokens.Generate();
        await db.Stacks
            .Where(s => s.Id == stackId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.AppApiToken, generated), ct);
        return generated;
    }

    private Task<string?> ReadTokenAsync(int stackId, CancellationToken ct) =>
        db.Stacks.AsNoTracking()
            .Where(s => s.Id == stackId)
            .Select(s => s.AppApiToken)
            .FirstOrDefaultAsync(ct);

    /// <summary>Projects the authenticated stack's identity.</summary>
    /// <param name="caller">The authenticated stack.</param>
    /// <returns>The stack's id, name and tenant slug.</returns>
    public static AppSelfDto GetSelf(AppApiCaller caller) =>
        new(caller.StackId, caller.Name, caller.TenantSlug);

    /// <summary>
    /// Lists the caller's own containers, resolved server-side from its compose project label.
    /// </summary>
    /// <remarks>
    /// Docker's label filter narrows the query to compose-managed containers server-side; the project
    /// value is then matched case-insensitively in process, matching how the update checker resolves
    /// stack membership. Results are ordered by service name then container id, so the order — and
    /// with it "the first container" — is stable across calls.
    /// </remarks>
    /// <param name="caller">The authenticated stack.</param>
    /// <param name="service">Optional <c>com.docker.compose.service</c> value to filter by.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stack's containers, in all states.</returns>
    public async Task<IReadOnlyList<DockerContainerInfo>> ListMemberContainersAsync(
        AppApiCaller caller, string? service, CancellationToken ct) {
        var containers = await docker.ListContainersByLabelsAsync([ProjectLabel], ct);
        return containers
            .Where(c => c.Labels is not null
                        && c.Labels.TryGetValue(ProjectLabel, out var project)
                        && string.Equals(project, caller.ComposeProjectName, StringComparison.OrdinalIgnoreCase))
            .Where(c => string.IsNullOrEmpty(service)
                        || string.Equals(ServiceOf(c), service, StringComparison.OrdinalIgnoreCase))
            .OrderBy(ServiceOf, StringComparer.Ordinal)
            .ThenBy(c => c.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Distinct compose service names present in a container list, sorted.</summary>
    /// <param name="containers">Containers to inspect.</param>
    /// <returns>Sorted, de-duplicated service names.</returns>
    public static IReadOnlyList<string> ServiceNames(IEnumerable<DockerContainerInfo> containers) =>
        containers.Select(ServiceOf).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Compose service name of a container, falling back to its container name (then id) for
    /// containers that carry the project label but no service label.
    /// </summary>
    private static string ServiceOf(DockerContainerInfo container) {
        if (container.Labels is not null
            && container.Labels.TryGetValue(ServiceLabel, out var service)
            && !string.IsNullOrEmpty(service))
            return service;
        var name = container.Names?.FirstOrDefault()?.TrimStart('/');
        return string.IsNullOrEmpty(name) ? container.Id : name;
    }

    /// <summary>
    /// Deployment and live runtime status of the calling stack.
    /// </summary>
    /// <param name="caller">The authenticated stack.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Last-deploy metadata, the in-flight deploy if any, and per-service container state.</returns>
    public async Task<AppStatusDto> GetStatusAsync(AppApiCaller caller, CancellationToken ct) {
        var containers = await ListMemberContainersAsync(caller, service: null, ct);
        var services = containers
            .Select(c => new AppServiceStatusDto(ServiceOf(c), c.Id, c.State, c.Status, c.Image))
            .ToList();

        // Newest in-flight event, with the identity column breaking a shared timestamp.
        var active = await db.DeployEvents.AsNoTracking()
            .Where(e => e.StackId == caller.StackId && (e.Status == "running" || e.Status == "queued"))
            .OrderByDescending(e => e.StartedAt)
            .ThenByDescending(e => e.Id)
            .Select(e => new { e.Id, e.Status, e.StartedAt })
            .FirstOrDefaultAsync(ct);

        return new AppStatusDto(
            MapDeployStatus(caller.LastDeployStatus),
            caller.LastDeployedAt,
            caller.LastDeployedCommit,
            active is null ? null : new AppActiveDeployDto(active.Id, MapEventStatus(active.Status), active.StartedAt),
            services);
    }

    /// <summary>
    /// Recent deploy events for the calling stack, newest first. The captured command output is
    /// never selected — it is produced with git and registry credentials in scope.
    /// </summary>
    /// <param name="caller">The authenticated stack.</param>
    /// <param name="limit">Requested page size; null uses <see cref="DefaultDeploymentLimit"/>, values are clamped to <see cref="MaxDeploymentLimit"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The requested page of deploy events.</returns>
    public async Task<AppDeploymentsDto> GetDeploymentsAsync(AppApiCaller caller, int? limit, CancellationToken ct) {
        var take = Math.Clamp(limit ?? DefaultDeploymentLimit, 1, MaxDeploymentLimit);
        var events = await db.DeployEvents.AsNoTracking()
            .Where(e => e.StackId == caller.StackId)
            .OrderByDescending(e => e.Id)
            .Take(take)
            .Select(e => new { e.Id, e.Status, e.TriggeredBy, e.StartedAt, e.FinishedAt })
            .ToListAsync(ct);

        return new AppDeploymentsDto(events
            .Select(e => new AppDeploymentDto(
                e.Id, MapEventStatus(e.Status), e.TriggeredBy, e.StartedAt, e.FinishedAt))
            .ToList());
    }

    /// <summary>
    /// What the stack is currently running: the commit of its last successful deploy plus the live
    /// image identity of each service container.
    /// </summary>
    /// <remarks>
    /// Images are inspected once per distinct image reference. An image that can no longer be
    /// inspected (pruned, or renamed under the running container) yields null id/digest rather than
    /// failing the whole response.
    /// </remarks>
    /// <param name="caller">The authenticated stack.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Commit, deploy timestamp and per-service image identity.</returns>
    public async Task<AppVersionDto> GetVersionAsync(AppApiCaller caller, CancellationToken ct) {
        var containers = await ListMemberContainersAsync(caller, service: null, ct);
        var inspected = new Dictionary<string, DockerImageInfo?>(StringComparer.Ordinal);

        var services = new List<AppVersionServiceDto>(containers.Count);
        foreach (var container in containers) {
            if (!inspected.TryGetValue(container.Image, out var image)) {
                image = await TryInspectImageAsync(container.Image, ct);
                inspected[container.Image] = image;
            }
            services.Add(new AppVersionServiceDto(
                ServiceOf(container), container.Image, image?.Id, ExtractDigest(image)));
        }

        return new AppVersionDto(caller.LastDeployedCommit, caller.LastDeployedAt, services);
    }

    private async Task<DockerImageInfo?> TryInspectImageAsync(string image, CancellationToken ct) {
        try {
            return await docker.InspectImageAsync(image, ct);
        } catch (HttpRequestException) {
            return null; // image no longer present locally — report the reference without identity
        }
    }

    /// <summary>Registry content digest from the first repo digest (<c>repo@sha256:…</c>), when present.</summary>
    private static string? ExtractDigest(DockerImageInfo? image) {
        var repoDigest = image?.RepoDigests.FirstOrDefault();
        if (string.IsNullOrEmpty(repoDigest)) return null;
        var at = repoDigest.IndexOf('@');
        return at < 0 ? null : repoDigest[(at + 1)..];
    }

    /// <summary>Maps the stack's stored deploy status onto the App API's lowercase vocabulary.</summary>
    /// <param name="status">Stored status; null before the first deploy.</param>
    /// <returns><c>queued</c>, <c>running</c>, <c>success</c> or <c>failed</c>; null when the input is null.</returns>
    public static string? MapDeployStatus(DeployStatus? status) => status switch {
        DeployStatus.Queued => "queued",
        DeployStatus.Running => "running",
        DeployStatus.Success => "success",
        DeployStatus.Failed => "failed",
        _ => null,
    };

    /// <summary>
    /// Maps a <c>deploy_events.status</c> string onto the same vocabulary. Anything unrecognized is
    /// reported as <c>failed</c> so the App API never leaks an internal state name.
    /// </summary>
    /// <param name="status">Stored deploy event status.</param>
    /// <returns><c>queued</c>, <c>running</c>, <c>success</c> or <c>failed</c>.</returns>
    public static string MapEventStatus(string? status) => status?.ToLowerInvariant() switch {
        "queued" => "queued",
        "running" => "running",
        "success" => "success",
        _ => "failed",
    };
}
