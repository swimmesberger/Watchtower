namespace Watchtower.Application.Services;

/// <summary>One image a release pins, as the pinning policy needs it.</summary>
/// <param name="Repository">
/// The canonical <c>{registry}/{repository}</c> the release recorded — already normalized by
/// <see cref="ImageRef"/> at intake, which is why the match below is an ordinal comparison and not a
/// second parse.
/// </param>
/// <param name="Digest">The manifest (index) digest, <c>sha256:…</c>.</param>
public sealed record ReleaseImageRef(string Repository, string Digest);

/// <summary>One compose service whose <c>image:</c> the deploy will rewrite.</summary>
/// <param name="ServiceName">The service within the stack.</param>
/// <param name="Image">
/// The full pinned reference, <c>{repository}@{digest}</c> — what the generated override writes and
/// what the deploy output names.
/// </param>
public sealed record ServiceImagePin(string ServiceName, string Image);

/// <summary>
/// Which of a stack's compose services a release pins, and the warnings the decision produced
/// (docs/products/design.md, "Image pinning").
/// </summary>
/// <remarks>
/// <para>
/// The runtime-neutral half of image pinning, exactly as <see cref="EnvInjectionPlan"/> is for
/// environment injection (ADR-0010's seam rule): it names no Docker or Compose concept, so a Kubernetes
/// engine could apply the same plan as container image fields. Turning a plan into a Compose override is
/// <c>ComposeOverrideFile</c>'s business, and it renders both plans into the one generated file.
/// </para>
/// <para>
/// Pure and total: it performs no I/O, never throws for bad input, and reports everything it could not
/// use as a warning. That matters because the alternative — failing the deploy — would take a whole
/// fleet down the moment somebody added a service or mistyped a label.
/// </para>
/// </remarks>
/// <param name="Services">
/// The services to rewrite, ordered by name; services left alone are absent. Deterministic so a rendered
/// override is diffable between deploys.
/// </param>
/// <param name="Warnings">
/// Operator-facing lines for the deploy output, in deterministic order. Digests are not secrets, so
/// these name everything.
/// </param>
public sealed record ImagePinPlan(
    IReadOnlyList<ServiceImagePin> Services,
    IReadOnlyList<string> Warnings) {
    /// <summary>A plan that pins nothing and has nothing to say — what a <c>Git</c>-mode deploy uses.</summary>
    public static readonly ImagePinPlan Empty = new([], []);

    /// <summary>
    /// Decides which services a release's images pin.
    /// </summary>
    /// <remarks>
    /// The match rule is repository identity: a service is pinned iff its image's
    /// <see cref="ImageRef.CanonicalRepository"/> equals one of the release's repositories. That is what
    /// makes <c>postgres:16</c> — <c>docker.io/library/postgres</c> — match nothing and stay untouched
    /// without any allowlist, and what lets <c>image: ghcr.io/acme/web:${TAG}</c> still match, because
    /// the engine resolved the interpolation before this ever saw it.
    /// <para>
    /// <c>watchtower.release-image</c> overrides the match, with the tri-state discipline of
    /// <see cref="EnvInjectionPlan.InjectTokenLabel"/>:
    /// <list type="bullet">
    /// <item><c>"false"</c> — never rewritten, <em>even on a match</em>: a service deliberately running a
    /// published tag.</item>
    /// <item><c>"true"</c> with no matching release image — a warning, and the deploy continues. Failing
    /// here would take a fleet down because somebody added a service to the compose file ahead of the
    /// build that produces its image.</item>
    /// <item>absent — match by repository.</item>
    /// <item>anything else — a warning, and treated as absent, because both guesses are wrong in a way
    /// the operator cannot see.</item>
    /// </list>
    /// A build-only service (no <c>image:</c>) is skipped silently: there is nothing to rewrite, and
    /// Compose builds it from the checkout the release's commit produced anyway. An image the engine
    /// reported but that does not parse is a warning rather than a silent skip — it is the case where
    /// pinning would otherwise stop working invisibly.
    /// </para>
    /// </remarks>
    /// <param name="services">The stack's services as the engine resolved them, in any order.</param>
    /// <param name="releaseImages">The images the target release pins; empty produces an empty plan.</param>
    /// <returns>The plan, ordered deterministically.</returns>
    public static ImagePinPlan Create(
        IReadOnlyList<EnvInjectionService> services, IReadOnlyList<ReleaseImageRef> releaseImages) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(releaseImages);
        if (services.Count == 0) return Empty;

        // Ordinal: both sides are already lower-cased canonical forms — the release's by ImageRef at
        // intake, the service's by CanonicalRepository below — so a case-insensitive comparison here
        // would only paper over a normalization bug rather than fix one.
        var byRepository = new Dictionary<string, ReleaseImageRef>(StringComparer.Ordinal);
        foreach (var image in releaseImages) byRepository[image.Repository] = image;

        var pins = new List<ServiceImagePin>();
        var warnings = new List<string>();

        foreach (var service in services.OrderBy(s => s.Name, StringComparer.Ordinal)) {
            var forced = ParseLabel(service, warnings);
            if (forced == false) continue;                       // explicit exemption wins over everything

            if (string.IsNullOrWhiteSpace(service.Image)) {
                // Build-only, and only worth a line when the author asked for a pin it cannot have.
                if (forced == true)
                    warnings.Add(
                        $"Warning: service '{service.Name}' is labelled {EnvInjectionPlan.ReleaseImageLabel} "
                        + "but builds its image rather than declaring one; nothing to pin.");
                continue;
            }

            if (!ImageRef.TryParse(service.Image, out var reference)) {
                warnings.Add(
                    $"Warning: could not read the image reference '{service.Image}' of service "
                    + $"'{service.Name}'; it was left as it is.");
                continue;
            }

            if (!byRepository.TryGetValue(reference.CanonicalRepository, out var match)) {
                if (forced == true)
                    warnings.Add(
                        $"Warning: service '{service.Name}' is labelled {EnvInjectionPlan.ReleaseImageLabel} "
                        + $"but this release has no image for {reference.CanonicalRepository}.");
                continue;
            }

            pins.Add(new ServiceImagePin(service.Name, $"{match.Repository}@{match.Digest}"));
        }

        return pins.Count == 0 && warnings.Count == 0 ? Empty : new ImagePinPlan(pins, warnings);
    }

    /// <summary>
    /// The label as a tri-state: true (force), false (exempt), null (absent or unusable). An unusable
    /// value is reported into <paramref name="warnings"/> and read as absent.
    /// </summary>
    private static bool? ParseLabel(EnvInjectionService service, List<string> warnings) {
        if (service.ReleaseImageLabel is not { } label) return null;
        // bool.TryParse accepts surrounding whitespace and any casing — the right tolerance for a
        // hand-written YAML label, and the same tolerance the inject-token label already has.
        if (bool.TryParse(label, out var parsed)) return parsed;
        warnings.Add(
            $"Warning: service '{service.Name}' has an unrecognized {EnvInjectionPlan.ReleaseImageLabel} "
            + $"value '{label}' — expected \"true\" or \"false\"; ignoring it.");
        return null;
    }
}
