using System.Runtime.CompilerServices;

namespace Watchtower.Api.Tests;

/// <summary>
/// Points <c>WATCHTOWER_DOCKER_CONFIG</c> at a directory that does not exist, before any test runs.
/// </summary>
/// <remarks>
/// <c>RegistryAuthBuilder</c> merges the <em>host's</em> docker config
/// (<c>~/.docker/config.json</c>) into every resolved-registry answer. On a developer machine that
/// layer is usually empty, but any environment that has logged into a registry — the GitHub Actions
/// runner ships with a <c>docker.io</c> credential for <c>githubactions</c> — leaks those
/// credentials into every test that touches registry resolution: the release-intake tests then see
/// real usernames where they asserted anonymous pulls, and fail only on CI. A module initializer
/// rather than a fixture because it must run before the first test constructs a host, and
/// unconditionally because no test has a legitimate use for the machine's real docker login.
/// </remarks>
internal static class DockerConfigIsolation {
    [ModuleInitializer]
    internal static void Isolate() =>
        Environment.SetEnvironmentVariable(
            "WATCHTOWER_DOCKER_CONFIG",
            Path.Combine(Path.GetTempPath(), $"watchtower-tests-no-docker-config-{Guid.NewGuid():N}"));
}
