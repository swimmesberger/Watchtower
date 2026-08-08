using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services;

/// <summary>
/// Resolves the Docker Compose project name Watchtower itself runs under, so no stack can be given
/// the same one.
/// </summary>
/// <remarks>
/// <para>
/// The App API resolves a caller's containers purely from the <c>com.docker.compose.project</c> label.
/// If a stack were created whose resolved project name matched Watchtower's own, that stack's token
/// would stream Watchtower's container logs — its deploy output, registry credentials in flight, and
/// every other stack's activity. Reserving the name closes that door.
/// </para>
/// <para>
/// The value is whatever label Watchtower's own container actually carries, read once via the
/// container inspect the self-update service already relies on (<c>HOSTNAME</c> → inspect) and cached
/// for the process lifetime — this sits on the stack create/update path, which must not pay for a
/// Docker round trip every call. Watchtower's own labels do not change without a restart, which
/// re-resolves. When Watchtower is not running in a container, or Docker cannot be reached, nothing
/// is reserved: this is a defense-in-depth layer, and failing to resolve it must never block stack
/// creation.
/// </para>
/// <para>
/// Only the compose project is reserved. The managed Caddy container is created directly over the
/// Docker API with no compose labels at all, so it is already invisible to the App API's
/// compose-project lookup and needs no entry here.
/// </para>
/// </remarks>
/// <param name="docker">Docker Engine API client used for the one-time self inspect.</param>
/// <param name="logger">Logger.</param>
public sealed class SelfProjectNameProvider(
    DockerEngineClient docker, ILogger<SelfProjectNameProvider> logger) {
    private const string ComposeProjectLabel = "com.docker.compose.project";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _projectName;
    private bool _resolved;

    /// <summary>
    /// Returns Watchtower's own compose project name, or null when it is not running under one (or
    /// could not be determined). Resolved once, then cached.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The reserved project name, or null when there is nothing to reserve.</returns>
    public async Task<string?> GetAsync(CancellationToken ct) {
        if (_resolved) return _projectName;

        await _gate.WaitAsync(ct);
        try {
            if (_resolved) return _projectName;
            _projectName = await ResolveAsync(ct);
            _resolved = true;
            return _projectName;
        } finally {
            _gate.Release();
        }
    }

    /// <summary>
    /// Reports whether <paramref name="projectName"/> is Watchtower's own project, ignoring case.
    /// </summary>
    /// <param name="projectName">Resolved compose project name to test.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the name belongs to Watchtower itself.</returns>
    public async Task<bool> IsReservedAsync(string projectName, CancellationToken ct) {
        var self = await GetAsync(ct);
        return !string.IsNullOrEmpty(self)
               && string.Equals(self, projectName, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<string?> ResolveAsync(CancellationToken ct) {
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(hostname)) return null;
        try {
            var details = await docker.InspectContainerAsync(hostname, ct);
            var project = details.Config.Labels.GetValueOrDefault(ComposeProjectLabel);
            if (!string.IsNullOrWhiteSpace(project))
                logger.LogInformation(
                    "Reserving compose project '{Project}' — Watchtower's own — against stack use", project);
            return string.IsNullOrWhiteSpace(project) ? null : project;
        } catch (Exception ex) {
            // Not in a container, or Docker unreachable: reserve nothing rather than block creation.
            logger.LogDebug(ex, "Could not determine Watchtower's own compose project name");
            return null;
        }
    }
}
