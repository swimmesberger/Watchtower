using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// Covers <see cref="EnvInjectionPlan"/> — the runtime-neutral policy deciding which service of a
/// stack receives which of Watchtower's reserved variables.
/// </summary>
/// <remarks>
/// Two of the three variables identify Watchtower and go everywhere; the third is a bearer token, and
/// every rule worth testing here is about where that one is allowed to land. The failure modes point
/// in opposite directions — injecting too widely hands a credential to containers that were never
/// meant to hold it, injecting too narrowly reproduces the silent 401 the whole feature exists to
/// remove — so both directions are pinned rather than just the happy path.
/// </remarks>
public sealed class EnvInjectionPlanTests {
    private const int StackId = 42;
    private const string Token = "wtapp_test-token";
    private const string BaseUrl = "https://watchtower.example.com";

    // ── The unconditional variables ──────────────────────────────────────────────────────────────

    [Fact]
    public void EveryServiceGetsTheStackId() {
        var plan = Plan([Service("web"), Service("worker"), Service("db")]);

        Assert.Equal(["db", "web", "worker"], plan.Services.Select(s => s.ServiceName));
        foreach (var service in plan.Services)
            Assert.Equal("42", Value(service, AppApiTokens.StackIdVariable));
    }

    [Fact]
    public void EveryServiceGetsTheUrlWhenAPublicBaseUrlIsConfigured() {
        var plan = Plan([Service("web"), Service("worker")], publicBaseUrl: $"  {BaseUrl}  ");

        foreach (var service in plan.Services)
            Assert.Equal(BaseUrl, Value(service, AppApiTokens.BaseUrlVariable));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoServiceGetsTheUrlWithoutOne(string? publicBaseUrl) {
        var plan = Plan([Service("web"), Service("worker")], publicBaseUrl);

        foreach (var service in plan.Services)
            Assert.DoesNotContain(AppApiTokens.BaseUrlVariable, service.Variables.Select(v => v.Key));
    }

    [Fact]
    public void AStackWithNoServicesPlansNothing() {
        var plan = Plan([]);

        Assert.Empty(plan.Services);
        Assert.Empty(plan.Warnings);
    }

    // ── The token: explicit opt-in ───────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyTheLabelledServicesGetTheToken() {
        var plan = Plan([Service("web", "true"), Service("worker"), Service("db")]);

        Assert.Equal(["web"], TokenRecipients(plan));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void SeveralServicesCanOptIn() {
        var plan = Plan([Service("web", "true"), Service("worker", "true"), Service("db")]);

        Assert.Equal(["web", "worker"], TokenRecipients(plan));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData(" true ")]
    public void TheOptInLabelIsReadLeniently(string label) {
        var plan = Plan([Service("web", label), Service("worker")]);

        Assert.Equal(["web"], TokenRecipients(plan));
        Assert.Empty(plan.Warnings);
    }

    /// <summary>
    /// An opt-in anywhere replaces the defaults entirely — the author has said which service wants the
    /// token, so a stack that would otherwise have had a default recipient no longer gets one implicitly.
    /// </summary>
    [Fact]
    public void AnOptInSuppressesTheTenantDefault() {
        var plan = Plan([Service("web"), Service("sidecar", "true")], target: "web");

        Assert.Equal(["sidecar"], TokenRecipients(plan));
    }

    // ── The token: explicit opt-out ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    public void TheOnlyServiceOfAStackCanOptOut(string label) {
        var plan = Plan([Service("web", label)]);

        Assert.Empty(TokenRecipients(plan));
        Assert.Empty(plan.Warnings);
        // Opting out of the credential does not opt out of knowing who Watchtower is.
        Assert.Equal([AppApiTokens.StackIdVariable], plan.Services.Single().Variables.Select(v => v.Key));
    }

    /// <summary>
    /// The opt-out is what a repository uses to keep the credential out of a container that Watchtower
    /// would otherwise have chosen by default — so it has to beat the tenant target, which is the one
    /// case where a stack gets the token without asking for it.
    /// </summary>
    [Fact]
    public void TheTenantTargetServiceCanOptOut() {
        var plan = Plan([Service("web", "false"), Service("worker")], target: "web");

        Assert.Empty(TokenRecipients(plan));
        Assert.Empty(plan.Warnings);
    }

    // ── The token: defaults ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASingleServiceStackGetsTheTokenWithoutAnyLabel() {
        var plan = Plan([Service("app")]);

        Assert.Equal(["app"], TokenRecipients(plan));
        Assert.Equal(
            [AppApiTokens.TokenVariable, AppApiTokens.StackIdVariable],
            plan.Services.Single().Variables.Select(v => v.Key));
    }

    /// <summary>
    /// With more than one service and nothing nominated there is no right answer, and the wrong one
    /// puts a credential somewhere the author never looked. The classic passthrough still works, so
    /// this is a fallback rather than a dead end.
    /// </summary>
    [Fact]
    public void AMultiServiceStackWithNoLabelGetsNoTokenAtAll() {
        var plan = Plan([Service("web"), Service("worker")]);

        Assert.Empty(TokenRecipients(plan));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void ATenantGetsTheTokenInItsTemplatesTargetService() {
        var plan = Plan([Service("web"), Service("worker"), Service("db")], target: "web");

        Assert.Equal(["web"], TokenRecipients(plan));
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void ATenantTargetNamingNoServiceIsReportedAndInjectsNothing() {
        var plan = Plan([Service("web"), Service("worker")], target: "frontend");

        Assert.Empty(TokenRecipients(plan));
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("frontend", warning);
        Assert.Contains(AppApiTokens.TokenVariable, warning);
    }

    /// <summary>
    /// The target names a service, so a one-service tenant whose template points elsewhere must not
    /// fall through to the "only one candidate" rule — that would inject into a service the template
    /// deliberately did not name.
    /// </summary>
    [Fact]
    public void AMissingTenantTargetDoesNotFallBackToTheSingleServiceRule() {
        var plan = Plan([Service("app")], target: "web");

        Assert.Empty(TokenRecipients(plan));
        Assert.Single(plan.Warnings);
    }

    // ── Unusable label values ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    [InlineData("maybe")]
    public void AnUnrecognizedLabelIsReportedAndTreatedAsAbsent(string label) {
        // Two services, so "treated as absent" is visible: the multi-service default injects nothing.
        var plan = Plan([Service("web", label), Service("worker")]);

        Assert.Empty(TokenRecipients(plan));
        var warning = Assert.Single(plan.Warnings);
        Assert.Contains("web", warning);
        Assert.Contains(EnvInjectionPlan.InjectTokenLabel, warning);
    }

    /// <summary>
    /// The surprising cell of the table, and intended: "unrecognized" means <em>absent</em>, and an
    /// absent label on the only service of a stack is the default single-service case. Someone who
    /// wrote <c>watchtower.inject-token: "no"</c> meaning "no" still gets the token — which is why the
    /// value is reported rather than quietly ignored.
    /// </summary>
    [Fact]
    public void AnUnrecognizedLabelOnASingleServiceStackStillTakesTheDefault() {
        var plan = Plan([Service("app", "no")]);

        Assert.Equal(["app"], TokenRecipients(plan));
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void AnUnrecognizedLabelStillLetsTheTenantDefaultApply() {
        var plan = Plan([Service("web"), Service("worker", "sometimes")], target: "web");

        Assert.Equal(["web"], TokenRecipients(plan));
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void EveryUnrecognizedLabelIsReportedOnceInServiceOrder() {
        var plan = Plan([Service("worker", "nope"), Service("web", "sure"), Service("db")]);

        Assert.Collection(plan.Warnings,
            first => Assert.Contains("'web'", first),
            second => Assert.Contains("'worker'", second));
    }

    // ── Determinism ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ServicesAndVariablesAreOrderedRegardlessOfInputOrder() {
        var plan = Plan(
            [Service("worker"), Service("db"), Service("web", "true")], publicBaseUrl: BaseUrl);

        Assert.Equal(["db", "web", "worker"], plan.Services.Select(s => s.ServiceName));
        Assert.Equal(
            [AppApiTokens.TokenVariable, AppApiTokens.StackIdVariable, AppApiTokens.BaseUrlVariable],
            plan.Services.Single(s => s.ServiceName == "web").Variables.Select(v => v.Key));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    private static EnvInjectionService Service(string name, string? label = null) => new(name, label);

    private static EnvInjectionPlan Plan(
        IReadOnlyList<EnvInjectionService> services, string? publicBaseUrl = null, string? target = null) =>
        EnvInjectionPlan.Create(new EnvInjectionRequest(services, StackId, Token, publicBaseUrl, target));

    /// <summary>The services the plan gives the App API token to, in plan order.</summary>
    private static IReadOnlyList<string> TokenRecipients(EnvInjectionPlan plan) => [
        .. plan.Services
            .Where(s => s.Variables.Any(v => v.Key == AppApiTokens.TokenVariable))
            .Select(s => s.ServiceName),
    ];

    private static string? Value(ServiceEnvInjection service, string key) =>
        service.Variables.FirstOrDefault(v => v.Key == key)?.Value;
}
