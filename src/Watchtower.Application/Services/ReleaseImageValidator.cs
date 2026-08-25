using Watchtower.Application.Entities;

namespace Watchtower.Application.Services;

/// <summary>How a pre-flight check of a release's images ended.</summary>
public enum ReleaseImageCheck {
    /// <summary>Every image is still in its registry.</summary>
    Present,

    /// <summary>At least one image is gone — <see cref="ReleaseImageValidation.Missing"/> names them.</summary>
    Missing,

    /// <summary>A registry could not be reached; nothing was concluded, and the same call may succeed later.</summary>
    Unavailable,
}

/// <summary>The outcome of one pre-flight check.</summary>
/// <param name="Status">What was concluded.</param>
/// <param name="Missing">
/// The <c>repository@digest</c> references the registry no longer has, in the order they were checked.
/// Empty unless <see cref="Status"/> is <see cref="ReleaseImageCheck.Missing"/>.
/// </param>
/// <param name="Unreachable">
/// The repositories whose registry did not answer. Empty unless <see cref="Status"/> is
/// <see cref="ReleaseImageCheck.Unavailable"/>.
/// </param>
public sealed record ReleaseImageValidation(
    ReleaseImageCheck Status, IReadOnlyList<string> Missing, IReadOnlyList<string> Unreachable) {
    /// <summary>Nothing to check — a release with no images, which intake cannot produce.</summary>
    public static readonly ReleaseImageValidation Present = new(ReleaseImageCheck.Present, [], []);
}

/// <summary>
/// The pre-flight a pin or a rollback runs before it changes anything: is every image of the target
/// release still in its registry? (docs/products/design.md, "Image pinning".)
/// </summary>
/// <remarks>
/// <para>
/// A pinned digest that was garbage-collected fails at <c>compose pull</c>, which is tolerable for the
/// automatic path — one stack, one failed deploy, one visible error. It is not tolerable for a rollback:
/// an operator reaching for an older release is usually already having a bad day, and discovering
/// halfway through that the images are gone is the worst possible moment. A pre-flight refusal naming
/// the missing reference beats a mid-rollback surprise.
/// </para>
/// <para>
/// Credentials come from the same resolved registry view a deploy pulls with and release intake
/// resolves against, through <see cref="KnownHosts"/> — one construction of "which registries does this
/// instance know", so a pin cannot be refused for an image intake happily accepted.
/// </para>
/// </remarks>
public class ReleaseImageValidator(RegistryAuthBuilder registries, IReleaseDigestResolver digests) {
    /// <summary>
    /// Total wall-clock budget for one pre-flight. The same shape and reasoning as intake's: the HEADs
    /// run in parallel, an operator is waiting on the answer, and "could not check, try again" is a
    /// better answer than a request that hangs.
    /// </summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>Checks that every image of <paramref name="release"/> is still in its registry.</summary>
    /// <param name="release">The release to pin; its <see cref="Release.Images"/> must be loaded.</param>
    /// <param name="ct">Cancellation token; the caller hanging up is not a registry problem.</param>
    public virtual async Task<ReleaseImageValidation> ValidateAsync(Release release, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(release);
        if (release.Images.Count == 0) return ReleaseImageValidation.Present;

        var known = KnownHosts(registries);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);

        var images = release.Images.OrderBy(i => i.Repository, StringComparer.Ordinal).ToList();
        var lookups = images.Select(async image => {
            var reference = $"{image.Repository}@{image.Digest}";
            // A repository stored by intake is already canonical, so the host is its first segment; an
            // unparseable one would have been refused at intake and is treated as credential-less here
            // rather than as a reason to fail the pin.
            var credential = ImageRef.TryParse(reference, out var parsed)
                ? known.GetValueOrDefault(parsed.Registry)
                : null;
            return (image, Result: await digests.ResolveAsync(
                reference, credential?.Username, credential?.Password, budget.Token));
        });
        var answers = await Task.WhenAll(lookups);
        ct.ThrowIfCancellationRequested();

        var missing = new List<string>();
        var unreachable = new List<string>();
        foreach (var (image, result) in answers) {
            switch (result.Status) {
                case ReleaseDigestStatus.NotFound:
                    missing.Add($"{image.Repository}@{image.Digest}");
                    break;
                case ReleaseDigestStatus.Unavailable:
                    unreachable.Add(image.Repository);
                    break;
            }
        }

        // "Gone" outranks "could not ask": if even one image is provably missing the pin cannot work,
        // and telling the operator to retry would only delay the same refusal.
        if (missing.Count > 0) return new ReleaseImageValidation(ReleaseImageCheck.Missing, missing, unreachable);
        return unreachable.Count > 0
            ? new ReleaseImageValidation(ReleaseImageCheck.Unavailable, [], unreachable)
            : ReleaseImageValidation.Present;
    }

    /// <summary>
    /// The registries this instance knows, keyed by normalized host: the host docker config merged with
    /// the configured registries, exactly the view a deploy pulls with.
    /// </summary>
    /// <remarks>
    /// Static and shared with <see cref="ReleaseIntakeService"/> so intake's registry gate and this
    /// pre-flight cannot disagree about which credential belongs to a host. Watchtower-configured
    /// entries win over host docker-config ones, matching the precedence
    /// <see cref="RegistryAuthBuilder.ListResolvedRegistries"/> itself applies when two spellings
    /// collapse onto one host.
    /// </remarks>
    public static Dictionary<string, ResolvedRegistry> KnownHosts(RegistryAuthBuilder registries) {
        ArgumentNullException.ThrowIfNull(registries);
        var known = new Dictionary<string, ResolvedRegistry>(StringComparer.Ordinal);
        foreach (var registry in registries.ListResolvedRegistries()) {
            var host = ImageRef.NormalizeRegistryHost(registry.Url);
            if (host.Length == 0) continue;
            if (known.TryGetValue(host, out var existing) && !existing.FromHostConfig) continue;
            known[host] = registry;
        }
        return known;
    }
}
