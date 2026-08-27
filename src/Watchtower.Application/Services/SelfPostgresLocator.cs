using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Watchtower.Application.Config;
using Watchtower.Application.Persistence;

namespace Watchtower.Application.Services;

/// <summary>
/// Watchtower's own database, as a container the dump machinery can exec into.
/// </summary>
/// <param name="ContainerId">Engine id — what every exec of the dump is addressed to.</param>
/// <param name="ContainerName">Operator-facing container name, for the run log and the manifest.</param>
/// <param name="Image">The image the container runs, recorded in the manifest.</param>
/// <param name="Host">The host the connection string names — how the container was recognized.</param>
/// <param name="Database">The database the connection string names.</param>
/// <param name="Username">The role the connection string connects as.</param>
public sealed record SelfPostgresTarget(
    string ContainerId, string ContainerName, string Image, string Host, string Database, string Username) {
    /// <summary>
    /// The dump target for this container. <c>watchtower</c> is its service identity inside the archive,
    /// so the SQL always lands at <c>backup/_dumps/watchtower.sql</c> whatever the container is called.
    /// No data volume and no mounted volumes: an instance archive carries the dump alone, so there is
    /// nothing for the exclusion to exclude it from.
    /// </summary>
    public DumpTarget ToDumpTarget() => new(
        ContainerId, ContainerName, SelfPostgresLocator.ServiceName, Image, DumpEngine.Postgres,
        DataVolume: null, MountedVolumes: []);
}

/// <summary>
/// Finds the container running Watchtower's own PostgreSQL, so the instance self-backup (ADR-0027) can
/// dump it with the same <c>pg_dumpall</c> machinery a stack's database goes through (ADR-0017).
/// </summary>
/// <remarks>
/// <para>
/// Watchtower cannot register itself as a stack — <see cref="SelfProjectNameProvider"/> reserves its own
/// compose project precisely so nothing can — so the container has to be found rather than configured.
/// The search is: the explicit setting if there is one; else the postgres-imaged containers of
/// Watchtower's own compose project; else, for an install that is not under Compose at all, every
/// running postgres-imaged container. In each case the connection string's <c>Host</c> is what picks the
/// winner out of the candidates, since that is the name Watchtower's own connections resolve.
/// </para>
/// <para>
/// Every failure throws with a message an operator can act on. A self-backup that quietly does nothing
/// because the database turned out to be managed, or because the daemon blipped, is worse than one that
/// fails loudly — the whole point of the feature is that the archive is there when the instance is not.
/// </para>
/// <para>
/// Not sealed, and <see cref="LocateAsync"/> virtual, for the reason the backup queue's enqueues are: a
/// test of what the restore <em>decides</em> should not need a Docker daemon to answer the one question
/// this class asks it.
/// </para>
/// </remarks>
public class SelfPostgresLocator(
    DockerEngineClient docker,
    SelfProjectNameProvider selfProjects,
    IConfiguration configuration,
    IOptionsMonitor<WatchtowerOptions> options,
    ILogger<SelfPostgresLocator> logger) {
    /// <summary>The compose label a container's project is stamped with.</summary>
    private const string ComposeProjectLabel = "com.docker.compose.project";

    /// <summary>The compose label a container's service name is stamped with.</summary>
    private const string ComposeServiceLabel = "com.docker.compose.service";

    /// <summary>The service identity Watchtower's own dump carries inside an archive.</summary>
    internal const string ServiceName = "watchtower";

    /// <summary>
    /// Locates the container, or throws explaining what to do about it.
    /// </summary>
    /// <param name="log">Receives operator-facing lines for the run output.</param>
    /// <param name="ct">The run's token.</param>
    /// <exception cref="InvalidOperationException">
    /// No connection string, no candidate container, or more than one candidate and nothing to choose by.
    /// </exception>
    public virtual async Task<SelfPostgresTarget> LocateAsync(Action<string> log, CancellationToken ct) {
        var connectionString = WatchtowerConnectionString.Find(configuration)
            ?? throw new InvalidOperationException(
                "No PostgreSQL connection string is configured, so there is no database to back up. "
                + $"Set '{WatchtowerConnectionString.ConfigurationKey}'.");

        NpgsqlConnectionStringBuilder parsed;
        try {
            parsed = new NpgsqlConnectionStringBuilder(connectionString);
        } catch (Exception ex) when (ex is ArgumentException or FormatException) {
            throw new InvalidOperationException(
                $"Watchtower's connection string could not be parsed ({ex.Message}), so its database "
                + "container cannot be identified.");
        }

        var host = parsed.Host ?? "";
        var database = string.IsNullOrEmpty(parsed.Database) ? "postgres" : parsed.Database;
        var username = parsed.Username ?? "postgres";

        if (options.CurrentValue.Backup.SelfPostgresContainer is { } configured
            && !string.IsNullOrWhiteSpace(configured))
            return await LocateConfiguredAsync(configured.Trim(), host, database, username, log, ct);

        var chosen = Choose(await CandidatesAsync(log, ct), host);

        var target = new SelfPostgresTarget(
            chosen.Id, DisplayName(chosen), chosen.Image, host, database, username);
        log($"Watchtower's database is container '{target.ContainerName}' ({target.Image}) "
            + $"— host '{host}', database '{database}'.");
        logger.LogInformation(
            "Located Watchtower's own database in container {ContainerId} ({Image})", chosen.Id, chosen.Image);
        return target;
    }

    /// <summary>
    /// Resolves the explicitly configured container. An operator who named one gets that one or an
    /// error — never a different container the detection happened to like better.
    /// </summary>
    private async Task<SelfPostgresTarget> LocateConfiguredAsync(
        string configured, string host, string database, string username, Action<string> log, CancellationToken ct) {
        DockerContainerDetails details;
        try {
            details = await docker.InspectContainerAsync(configured, ct);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            throw new InvalidOperationException(
                $"The container '{configured}' named in {WatchtowerSettingPaths.BackupSelfPostgresContainer} "
                + $"could not be inspected: {ex.Message}");
        }
        if (!string.Equals(details.State?.Status, "running", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"The container '{configured}' named in {WatchtowerSettingPaths.BackupSelfPostgresContainer} "
                + $"is {details.State?.Status ?? "in an unknown state"} — pg_dumpall needs a live server.");

        var name = configured;
        if (!DatabaseDumpTargets.IsPostgresImage(details.Config.Image))
            // Not fatal: the list of known repositories is not the list of images that run a server, and
            // an operator who named this container meant it. The preflight proves it either way.
            log($"WARNING: '{name}' runs {details.Config.Image}, which is not a recognized PostgreSQL "
                + "image — trying it anyway, since it is the configured container.");

        log($"Watchtower's database is container '{name}' ({details.Config.Image}), from "
            + $"{WatchtowerSettingPaths.BackupSelfPostgresContainer} — database '{database}'.");
        return new SelfPostgresTarget(details.Id, name, details.Config.Image, host, database, username);
    }

    /// <summary>
    /// The running PostgreSQL containers worth considering: Watchtower's own compose project when it has
    /// one, else every running container on the daemon (a <c>docker run</c> install has no project to
    /// narrow by, and narrowing by nothing is better than finding nothing).
    /// </summary>
    private async Task<IReadOnlyList<DockerContainerInfo>> CandidatesAsync(Action<string> log, CancellationToken ct) {
        var project = await selfProjects.GetAsync(ct);
        var containers = project is { Length: > 0 }
            ? await docker.ListContainersByLabelsAsync([$"{ComposeProjectLabel}={project}"], ct)
            : await docker.ListContainersAsync(ct);
        if (project is not { Length: > 0 })
            log("Watchtower is not running under a Compose project, so every running PostgreSQL "
                + "container on this daemon is a candidate for its database.");
        return [
            .. containers.Where(c =>
                string.Equals(c.State, "running", StringComparison.OrdinalIgnoreCase)
                && DatabaseDumpTargets.IsPostgresImage(c.Image)),
        ];
    }

    /// <summary>
    /// Picks the one container that holds Watchtower's database out of the PostgreSQL containers on the
    /// daemon, or throws saying why it could not. Pure, so the whole rule is testable without a daemon.
    /// </summary>
    /// <remarks>
    /// The connection string's host is the name Watchtower's own connection pool resolves, so a container
    /// that answers to it <em>is</em> the database rather than merely <em>a</em> database. When nothing
    /// answers to it, a single candidate is still unambiguous — a Compose install whose service is
    /// aliased differently from the host is ordinary — but several are not, and guessing which one holds
    /// the instance's own state is precisely the wrong thing to guess: the loser would be dumped, and the
    /// dump would look like a good backup.
    /// </remarks>
    /// <param name="candidates">The running PostgreSQL containers to choose from.</param>
    /// <param name="host">The host Watchtower's connection string names.</param>
    /// <exception cref="InvalidOperationException">No candidate, or no way to tell them apart.</exception>
    internal static DockerContainerInfo Choose(IReadOnlyList<DockerContainerInfo> candidates, string host) {
        if (candidates.Count == 0) throw new InvalidOperationException(NoCandidateMessage(host));

        var matched = candidates.Where(c => AnswersTo(c, host)).ToList();
        return matched switch {
            [var only] => only,
            { Count: > 1 } => throw new InvalidOperationException(
                $"More than one PostgreSQL container answers to the host '{host}' that Watchtower's "
                + $"connection string names ({string.Join(", ", matched.Select(DisplayName))}). "
                + $"Name the right one in {WatchtowerSettingPaths.BackupSelfPostgresContainer}."),
            _ => candidates switch {
                [var only] => only,
                _ => throw new InvalidOperationException(
                    $"None of the PostgreSQL containers on this daemon answers to the host '{host}' that "
                    + "Watchtower's connection string names, and there is more than one to choose from "
                    + $"({string.Join(", ", candidates.Select(DisplayName))}). "
                    + $"Name the right one in {WatchtowerSettingPaths.BackupSelfPostgresContainer}."),
            },
        };
    }

    /// <summary>
    /// Whether <paramref name="container"/> is reachable under <paramref name="host"/>: its compose
    /// service (what a container resolves a sibling by), its container name, or the
    /// <c>{project}-{service}-{n}</c> name Compose generates from that service.
    /// </summary>
    private static bool AnswersTo(DockerContainerInfo container, string host) {
        if (string.IsNullOrEmpty(host)) return false;
        if (Same(container.Labels.GetValueOrDefault(ComposeServiceLabel), host)) return true;
        foreach (var raw in container.Names) {
            // Compose names a container "{project}-{service}-{replica}", so both ends have to come off
            // before what is left can be compared to the service the connection string names.
            var name = StripReplicaIndex(raw.TrimStart('/'));
            if (Same(name, host) || name.EndsWith($"-{host}", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// <c>watchtower-postgres-1</c> → <c>watchtower-postgres</c>; anything not ending in a numeric
    /// segment is returned as it is (a container the operator named by hand keeps its name).
    /// </summary>
    private static string StripReplicaIndex(string name) {
        var lastDash = name.LastIndexOf('-');
        return lastDash > 0 && int.TryParse(name.AsSpan(lastDash + 1), out _) ? name[..lastDash] : name;
    }

    private static bool Same(string? left, string right) =>
        left is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string DisplayName(DockerContainerInfo container) =>
        container.Names.FirstOrDefault()?.TrimStart('/') ?? container.Id;

    /// <summary>
    /// The message for "there is no container to dump". Names both reasons it can be true, because from
    /// here they look identical: the database really is somewhere else, or the daemon did not answer.
    /// </summary>
    private static string NoCandidateMessage(string host) =>
        $"No running PostgreSQL container was found for the host '{host}' in Watchtower's connection "
        + "string. Watchtower can only back up its own database when that database runs as a container "
        + "on this Docker daemon — a managed or host-installed PostgreSQL has to be backed up by "
        + "whoever operates it. If it is a container, name it in "
        + $"{WatchtowerSettingPaths.BackupSelfPostgresContainer}. "
        + "(This is also what you see when the Docker daemon could not be reached at all.)";
}
