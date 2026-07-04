namespace Watchtower.Application.Config;

/// <summary>
/// Strongly-typed configuration options for Watchtower.
/// Bound from the "Watchtower" section of appsettings.json or environment variables
/// (e.g. WATCHTOWER__DBPATH, WATCHTOWER__DOCKERAPIVERSION).
/// </summary>
public sealed record WatchtowerOptions {
    /// <summary>Path to the SQLite database file.</summary>
    public string DbPath { get; init; } = "/data/watchtower.db";

    /// <summary>
    /// Docker Engine API version used for all Docker communication.
    /// <list type="bullet">
    ///   <item><description>
    ///     Direct API calls (<see cref="Services.DockerEngineClient"/>) use it as the URL segment: <c>/v1.43/containers/…</c>
    ///   </description></item>
    ///   <item><description>
    ///     <c>docker compose</c> subprocesses (<see cref="Services.ComposeCliService"/>) receive it via the
    ///     <c>DOCKER_API_VERSION</c> environment variable, preventing the compose CLI from
    ///     auto-negotiating a version newer than the daemon supports.
    ///   </description></item>
    /// </list>
    /// Update this if your Docker daemon supports a newer API version.
    /// </summary>
    public string DockerApiVersion { get; init; } = "1.43";

    /// <summary>
    /// When true, a background service periodically checks for a newer Watchtower image
    /// so the UI badge stays up to date without a manual check.
    /// Set via <c>WATCHTOWER__AUTOCHECKENABLED=true</c> or appsettings.json.
    /// Defaults to false so no outbound registry traffic is generated unless opted in.
    /// </summary>
    public bool AutoCheckEnabled { get; init; } = false;

    /// <summary>
    /// How often the background auto-check runs, in minutes. Clamped to 1–1440.
    /// Only relevant when <see cref="AutoCheckEnabled"/> is true.
    /// </summary>
    public int AutoCheckIntervalMinutes { get; init; } = 5;

    /// <summary>
    /// When true, a background service periodically checks whether any container image in
    /// each stack has a newer version available in the registry.
    /// Set via <c>WATCHTOWER__STACKCHECKENABLED=true</c> or appsettings.json.
    /// Defaults to false so no outbound registry traffic is generated unless opted in.
    /// </summary>
    public bool StackCheckEnabled { get; init; } = false;

    /// <summary>
    /// How often the stack update background check runs, in minutes. Clamped to 1–1440.
    /// Only relevant when <see cref="StackCheckEnabled"/> is true.
    /// </summary>
    public int StackCheckIntervalMinutes { get; init; } = 15;
}
