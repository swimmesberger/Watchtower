using System.Runtime.CompilerServices;
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
/// The value is whatever label Watchtower's own container actually carries, read via the container
/// inspect the self-update service already relies on (<c>HOSTNAME</c> → inspect). It sits on the stack
/// create/update path, which must not pay for a Docker round trip every call, so the answer is cached
/// — but <em>only a conclusive one</em>. An unset <c>HOSTNAME</c> and a successful inspect (including
/// one that finds no compose project label) are conclusive and latch for the process lifetime;
/// Watchtower's own labels do not change without a restart, which re-resolves. A Docker or transport
/// failure is <em>inconclusive</em> and is never cached, so a daemon blip during the first
/// <c>stacks.create</c> cannot silently disable the reservation for the rest of the process — the next
/// call simply retries.
/// </para>
/// <para>
/// While unresolved, nothing is reserved. This is a defense-in-depth layer on top of the
/// stack-to-stack uniqueness check, and failing to resolve it must never block stack creation.
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

    /// <summary>
    /// The conclusively resolved answer, or null while still unresolved. A single volatile reference
    /// write publishes the boxed value, so readers outside the lock cannot observe a torn or stale
    /// result: the box's contents are written before it is published, and the volatile read acquires.
    /// The box is needed because the resolved value may itself legitimately be null.
    /// </summary>
    private volatile StrongBox<string?>? _resolved;

    /// <summary>
    /// Returns Watchtower's own compose project name, or null when it is not running under one — or
    /// when that could not be determined yet.
    /// </summary>
    /// <remarks>
    /// Conclusive answers are cached for the process lifetime; an inconclusive one (Docker
    /// unreachable) is not, so the next call retries rather than leaving the reservation permanently
    /// disabled by a transient outage.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The reserved project name, or null when there is nothing to reserve.</returns>
    public async Task<string?> GetAsync(CancellationToken ct) {
        if (_resolved is { } cached) return cached.Value;

        await _gate.WaitAsync(ct);
        try {
            if (_resolved is { } current) return current.Value;
            try {
                var value = await ResolveAsync(ct);
                // Latch only now: reaching here means the question was actually answered.
                _resolved = new StrongBox<string?>(value);
                return value;
            } catch (Exception ex) when (ex is not OperationCanceledException) {
                // Inconclusive — Docker unreachable or the inspect failed. Deliberately not cached.
                logger.LogDebug(ex, "Could not determine Watchtower's own compose project name; will retry");
                return null;
            }
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

    /// <summary>
    /// Performs the actual lookup. Returns only conclusive answers; a failed inspect is allowed to
    /// throw so the caller can tell "there is nothing to reserve" apart from "we could not find out",
    /// and cache only the former.
    /// </summary>
    private async Task<string?> ResolveAsync(CancellationToken ct) {
        // No HOSTNAME means Watchtower is not running as a container: conclusively nothing to reserve.
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME");
        if (string.IsNullOrWhiteSpace(hostname)) return null;

        // A failure here propagates on purpose — see the summary above.
        var details = await docker.InspectContainerAsync(hostname, ct);

        var project = details.Config.Labels.GetValueOrDefault(ComposeProjectLabel);
        if (string.IsNullOrWhiteSpace(project)) return null; // running standalone, not under Compose

        logger.LogInformation(
            "Reserving compose project '{Project}' — Watchtower's own — against stack use", project);
        return project;
    }
}
