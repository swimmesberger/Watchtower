using System.Globalization;

namespace Watchtower.Application.Services;

/// <summary>
/// One deployable service the injection policy can target, together with the value of its
/// <see cref="EnvInjectionPlan.InjectTokenLabel"/> label as the runtime engine read it.
/// </summary>
/// <param name="Name">The service's name within the stack.</param>
/// <param name="InjectTokenLabel">
/// Raw label value, or null when the service carries no such label. Parsing (and the warning for an
/// unusable value) belongs to the policy, so the engine passes the text through untouched.
/// </param>
public sealed record EnvInjectionService(string Name, string? InjectTokenLabel = null);

/// <summary>A single environment variable a service is to receive.</summary>
/// <param name="Key">Variable name.</param>
/// <param name="Value">Variable value; never written to logs or deploy output.</param>
public sealed record InjectedEnvVar(string Key, string Value) {
    /// <summary>
    /// Redacted: one of these carries the App API token, and the record's generated <c>ToString</c>
    /// would print it the first time a plan reached a log message or an exception. Names are what the
    /// deploy output is allowed to say — see <see cref="AppApiTokens"/>.
    /// </summary>
    public override string ToString() => $"{Key}=***";
}

/// <summary>Everything one service receives, ordered by <see cref="InjectedEnvVar.Key"/>.</summary>
/// <param name="ServiceName">The service the variables go to.</param>
/// <param name="Variables">The variables, in deterministic key order.</param>
public sealed record ServiceEnvInjection(string ServiceName, IReadOnlyList<InjectedEnvVar> Variables);

/// <summary>The inputs <see cref="EnvInjectionPlan.Create"/> decides from.</summary>
/// <param name="Services">The stack's services, in any order.</param>
/// <param name="StackId">Watchtower's numeric stack id.</param>
/// <param name="AppApiToken">The stack's App API bearer token.</param>
/// <param name="PublicBaseUrl">
/// Configured <c>Watchtower:PublicBaseUrl</c>, or null/blank when Watchtower has no publicly
/// reachable address — in which case <c>WATCHTOWER_URL</c> is injected nowhere.
/// </param>
/// <param name="TargetServiceName">
/// For a tenant stack, its template's target service — the default recipient of the token. Null for a
/// standalone stack.
/// </param>
public sealed record EnvInjectionRequest(
    IReadOnlyList<EnvInjectionService> Services,
    int StackId,
    string AppApiToken,
    string? PublicBaseUrl = null,
    string? TargetServiceName = null) {
    /// <summary>Redacted for the same reason as <see cref="InjectedEnvVar.ToString"/>: it carries the token.</summary>
    public override string ToString() =>
        $"{nameof(EnvInjectionRequest)} {{ StackId = {StackId}, Services = {Services.Count} }}";
}

/// <summary>
/// Which of Watchtower's reserved variables each service of a stack receives, and the warnings the
/// decision produced. Computed by <see cref="Create"/>.
/// </summary>
/// <remarks>
/// <para>
/// This type is the runtime-neutral half of direct environment injection (ADR-0010's seam rule): it
/// names no Docker or Compose concept, so a future Kubernetes engine can apply the same plan as pod
/// environment entries. Turning a plan into a Compose override file is the Docker engine's private
/// business and lives in <c>ComposeOverrideFile</c>.
/// </para>
/// <para>
/// The policy exists because <c>WATCHTOWER_APP_TOKEN</c> is a credential — and for a stack holding
/// management grants, a powerful one. <c>WATCHTOWER_STACK_ID</c> and <c>WATCHTOWER_URL</c> identify
/// Watchtower rather than authenticate to it, so they go everywhere; the token is scoped to the one
/// service that is meant to call back.
/// </para>
/// </remarks>
/// <param name="Services">
/// The services receiving something, ordered by name; each one's variables ordered by key. Services
/// receiving nothing are absent. Deterministic so a rendered plan is diffable.
/// </param>
/// <param name="Warnings">
/// Operator-facing lines to surface in the deploy output, in deterministic order. Never contains a
/// variable value.
/// </param>
public sealed record EnvInjectionPlan(
    IReadOnlyList<ServiceEnvInjection> Services,
    IReadOnlyList<string> Warnings) {
    /// <summary>
    /// Per-service label choosing whether the service receives <c>WATCHTOWER_APP_TOKEN</c>:
    /// <c>"true"</c> opts in, <c>"false"</c> opts out, anything else is ignored with a warning.
    /// </summary>
    public const string InjectTokenLabel = "watchtower.inject-token";

    /// <summary>A plan that injects nothing — no services, no warnings.</summary>
    public static readonly EnvInjectionPlan Empty = new([], []);

    /// <summary>
    /// Applies the injection policy: <c>WATCHTOWER_STACK_ID</c> to every service, <c>WATCHTOWER_URL</c>
    /// to every service when a public base URL is configured, and <c>WATCHTOWER_APP_TOKEN</c> only where
    /// the rules below place it.
    /// </summary>
    /// <remarks>
    /// The token's recipients, in order of precedence:
    /// <list type="number">
    /// <item>every service labelled <c>watchtower.inject-token: "true"</c> — an explicit opt-in that
    /// suppresses the defaults below entirely;</item>
    /// <item>otherwise the default recipient: a tenant's template target service, or the single service
    /// of a one-service standalone stack. A multi-service standalone stack has no unambiguous default
    /// and therefore gets nothing;</item>
    /// <item>never a service labelled <c>"false"</c>, which is the opt-out for a repository that does
    /// not want the credential in that container at all.</item>
    /// </list>
    /// A label value that is neither <c>"true"</c> nor <c>"false"</c> is treated as absent rather than
    /// guessed at, because both guesses are wrong in a way the operator cannot see: assuming opt-in
    /// leaks the credential, assuming opt-out produces the silent 401 this feature exists to remove.
    /// It is reported instead.
    /// </remarks>
    /// <param name="request">The stack's services and the values available for injection.</param>
    /// <returns>The plan, ordered deterministically.</returns>
    public static EnvInjectionPlan Create(EnvInjectionRequest request) {
        var services = request.Services
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
        if (services.Count == 0) return Empty;

        var warnings = new List<string>();
        var optIn = new HashSet<string>(StringComparer.Ordinal);
        var optOut = new HashSet<string>(StringComparer.Ordinal);
        foreach (var service in services) {
            if (service.InjectTokenLabel is not { } label) continue;
            // bool.TryParse accepts surrounding whitespace and any casing, which is the right amount of
            // tolerance for a hand-written YAML label; everything else is reported, not interpreted.
            if (bool.TryParse(label, out var optedIn))
                (optedIn ? optIn : optOut).Add(service.Name);
            else
                warnings.Add(
                    $"Warning: service '{service.Name}' has an unrecognized {InjectTokenLabel} value "
                    + $"'{label}' — expected \"true\" or \"false\"; ignoring it.");
        }

        var tokenRecipients = ResolveTokenRecipients(request, services, optIn, optOut, warnings);

        var stackId = request.StackId.ToString(CultureInfo.InvariantCulture);
        var baseUrl = string.IsNullOrWhiteSpace(request.PublicBaseUrl) ? null : request.PublicBaseUrl.Trim();

        var planned = new List<ServiceEnvInjection>(services.Count);
        foreach (var service in services) {
            var variables = new List<InjectedEnvVar>(3);
            if (tokenRecipients.Contains(service.Name))
                variables.Add(new InjectedEnvVar(AppApiTokens.TokenVariable, request.AppApiToken));
            variables.Add(new InjectedEnvVar(AppApiTokens.StackIdVariable, stackId));
            if (baseUrl is not null)
                variables.Add(new InjectedEnvVar(AppApiTokens.BaseUrlVariable, baseUrl));
            planned.Add(new ServiceEnvInjection(
                service.Name, [.. variables.OrderBy(v => v.Key, StringComparer.Ordinal)]));
        }

        return new EnvInjectionPlan(planned, warnings);
    }

    /// <summary>Resolves which services receive the App API token, appending any warning it produces.</summary>
    private static IReadOnlySet<string> ResolveTokenRecipients(
        EnvInjectionRequest request,
        IReadOnlyList<EnvInjectionService> services,
        HashSet<string> optIn,
        HashSet<string> optOut,
        List<string> warnings) {
        if (optIn.Count > 0) return optIn;

        // A tenant's template names its target service; that is the service the tenant-switcher and
        // management calls come from, so it is the default recipient.
        if (!string.IsNullOrWhiteSpace(request.TargetServiceName)) {
            var target = request.TargetServiceName.Trim();
            if (!services.Any(s => string.Equals(s.Name, target, StringComparison.Ordinal))) {
                warnings.Add(
                    $"Warning: the template's target service '{target}' is not one of this stack's "
                    + $"services — {AppApiTokens.TokenVariable} was not injected by default (label a "
                    + $"service with {InjectTokenLabel}: \"true\" to choose one).");
                return Recipients();
            }
            return optOut.Contains(target) ? Recipients() : Recipients(target);
        }

        // A single-service stack has exactly one candidate, so there is nothing to get wrong. With more
        // than one, guessing would put the credential in a container the author never nominated.
        //
        // "How many services" is whatever the engine reported, not what the stack file declares: which
        // profile-gated services a `docker compose config` lists has varied across Compose versions, so
        // a stack whose second service sits behind a profile may or may not be counted as single. The
        // count is deliberately taken as reported rather than reconstructed — the label is the way to
        // say which service wants the token when that distinction matters.
        if (services.Count == 1 && !optOut.Contains(services[0].Name)) return Recipients(services[0].Name);
        return Recipients();

        static IReadOnlySet<string> Recipients(params string[] names) =>
            new HashSet<string>(names, StringComparer.Ordinal);
    }
}
