using Microsoft.Extensions.Logging;

namespace Watchtower.Application.Services;

/// <summary>How a tag lookup against a registry ended.</summary>
public enum ReleaseDigestStatus {
    /// <summary>The registry answered with a manifest digest.</summary>
    Resolved,

    /// <summary>The registry answered, and the reference is not there — a typo or an unpushed tag.</summary>
    NotFound,

    /// <summary>The registry could not be reached, or did not answer inside the budget.</summary>
    Unavailable,
}

/// <summary>One tag lookup's outcome. <see cref="Digest"/> is set only for <see cref="ReleaseDigestStatus.Resolved"/>.</summary>
public sealed record ReleaseDigestResult(ReleaseDigestStatus Status, string? Digest) {
    public static ReleaseDigestResult Resolved(string digest) => new(ReleaseDigestStatus.Resolved, digest);
    public static readonly ReleaseDigestResult NotFound = new(ReleaseDigestStatus.NotFound, null);
    public static readonly ReleaseDigestResult Unavailable = new(ReleaseDigestStatus.Unavailable, null);
}

/// <summary>
/// The one call release intake makes out of the process: a registry manifest lookup, turned into a
/// three-way outcome so the caller can answer 400 for a tag that is not there and 503 for a registry
/// that did not answer.
/// </summary>
/// <remarks>
/// An interface for the same reason <see cref="StackUpdateService"/> has virtual Docker seams: this is
/// the only part of intake that leaves the machine, and a test that had to reach a real registry to
/// check the fingerprint rule would be a test that fails on a train.
/// </remarks>
public interface IReleaseDigestResolver {
    /// <summary>Resolves one image reference to its manifest digest.</summary>
    /// <param name="imageReference">The reference as written, e.g. <c>ghcr.io/acme/api:sha-a1b2c3d</c>.</param>
    /// <param name="username">Registry user name from the resolved registry view, when there is one.</param>
    /// <param name="password">Registry password paired with <paramref name="username"/>.</param>
    /// <param name="ct">The intake budget; cancellation is reported as <see cref="ReleaseDigestStatus.Unavailable"/>.</param>
    Task<ReleaseDigestResult> ResolveAsync(
        string imageReference, string? username, string? password, CancellationToken ct);
}

/// <summary>
/// The shipped resolver: <c>HEAD /v2/{name}/manifests/{reference}</c> through
/// <see cref="DockerEngineClient.GetRemoteDigestAsync"/>.
/// </summary>
/// <remarks>
/// The classification is what this adds over that call, which answers null for both "not there" and
/// "could not ask". A transport failure throws out of it, so it is caught here and reported as
/// <see cref="ReleaseDigestStatus.Unavailable"/>; a null return means the registry answered and had
/// nothing, which is <see cref="ReleaseDigestStatus.NotFound"/>. Getting this the wrong way round would
/// turn a registry outage into a release rejected for a tag that exists.
/// </remarks>
public sealed class RegistryDigestResolver(DockerEngineClient docker, ILogger<RegistryDigestResolver> logger)
    : IReleaseDigestResolver {
    public async Task<ReleaseDigestResult> ResolveAsync(
        string imageReference, string? username, string? password, CancellationToken ct) {
        try {
            var digest = await docker.GetRemoteDigestAsync(imageReference, username, password, ct);
            return string.IsNullOrWhiteSpace(digest)
                ? ReleaseDigestResult.NotFound
                : ReleaseDigestResult.Resolved(digest);
        } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) {
            // Deliberately including OperationCanceledException: the only token this is ever given is
            // the intake budget, and the caller re-checks its own cancellation before reading results.
            logger.LogDebug(ex, "Release intake could not reach the registry for {Image}", imageReference);
            return ReleaseDigestResult.Unavailable;
        }
    }
}
