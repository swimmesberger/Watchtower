using Watchtower.Application.Entities;
using Watchtower.Application.Services;
using Xunit;

namespace Watchtower.Application.Tests;

/// <summary>
/// The backup policy ladder of design.md §"Backups across tenants": <b>compose label &gt; stack override
/// &gt; template policy &gt; instance default</b>. The label rung is per service and belongs to
/// <see cref="BackupPlan"/> (<see cref="BackupPlanOverrideTests"/> pins it); the three rungs below it are
/// per stack and live in <see cref="BackupPolicyResolver"/>, which is what this suite exercises.
/// </summary>
/// <remarks>
/// <b>Every rung of every field gets its own assertion, and every one is worth mutation-checking.</b>
/// The four fields resolve independently — a tenant may take its schedule from the fleet while
/// overriding the quiesce mode — so a resolver that read the wrong rung for one of them would leave the
/// other three looking right. The tri-state cases matter for the same reason the migration preserves
/// explicit values: <c>false</c> and "unset" are different answers, and collapsing them would silently
/// enrol (or drop) whole fleets.
/// </remarks>
public sealed class BackupPolicyResolverTests {
    private static Stack Stack(
        bool? enabled = null, bool? stopContainers = null, string? cron = null,
        BackupQuiesceMode? quiesceMode = null) => new() {
        Name = "tenant", ComposeProjectName = "tenant",
        BackupEnabled = enabled,
        BackupStopContainers = stopContainers,
        BackupCron = cron,
        BackupQuiesceMode = quiesceMode,
    };

    private static StackTemplate Template(
        bool? enabled = null, bool? stopContainers = null, string? cron = null,
        BackupQuiesceMode? quiesceMode = null) => new() {
        Name = "fleet", DomainPattern = "{tenant}.example.com", TargetServiceName = "web",
        BackupEnabled = enabled,
        BackupStopContainers = stopContainers,
        BackupCron = cron,
        BackupQuiesceMode = quiesceMode,
    };

    // ── Rung 4: the instance default ─────────────────────────────────────────

    /// <summary>Nothing set anywhere: the defaults are the pre-stage-7 behaviour of every stack.</summary>
    [Fact]
    public void ASilentStackWithNoTemplate_TakesTheInstanceDefaults() {
        var policy = BackupPolicyResolver.Resolve(Stack(), template: null);

        Assert.False(policy.Enabled);
        Assert.True(policy.StopContainers);
        Assert.Equal(BackupQuiesceMode.Stop, policy.QuiesceMode);
        Assert.Null(policy.Cron);
        Assert.Equal(BackupPolicySource.Instance, policy.EnabledSource);
        Assert.Equal(BackupPolicySource.Instance, policy.StopContainersSource);
        Assert.Equal(BackupPolicySource.Instance, policy.QuiesceModeSource);
        Assert.Equal(BackupPolicySource.Instance, policy.CronSource);
    }

    /// <summary>A template that says nothing is not a rung — it must not shadow the instance default.</summary>
    [Fact]
    public void ASilentTemplate_LeavesEveryFieldOnTheInstanceDefault() {
        var policy = BackupPolicyResolver.Resolve(Stack(), Template());

        Assert.False(policy.Enabled);
        Assert.True(policy.StopContainers);
        Assert.Equal(BackupQuiesceMode.Stop, policy.QuiesceMode);
        Assert.Null(policy.Cron);
        Assert.Equal(BackupPolicySource.Instance, policy.EnabledSource);
        Assert.Equal(BackupPolicySource.Instance, policy.CronSource);
    }

    // ── Rung 3: the template policy ──────────────────────────────────────────

    [Fact]
    public void ATemplateValue_ReachesATenantThatSaysNothing() {
        var policy = BackupPolicyResolver.Resolve(
            Stack(),
            Template(enabled: true, stopContainers: false, cron: "0 4 * * *", quiesceMode: BackupQuiesceMode.Pause));

        Assert.True(policy.Enabled);
        Assert.False(policy.StopContainers);
        Assert.Equal("0 4 * * *", policy.Cron);
        Assert.Equal(BackupQuiesceMode.Pause, policy.QuiesceMode);
        Assert.Equal(BackupPolicySource.Template, policy.EnabledSource);
        Assert.Equal(BackupPolicySource.Template, policy.StopContainersSource);
        Assert.Equal(BackupPolicySource.Template, policy.CronSource);
        Assert.Equal(BackupPolicySource.Template, policy.QuiesceModeSource);
    }

    /// <summary>A standalone stack has no template rung at all, so it drops straight to the instance.</summary>
    [Fact]
    public void AStandaloneStack_NeverSeesATemplatePolicy() {
        var policy = BackupPolicyResolver.Resolve(Stack(), template: null);

        Assert.False(policy.Enabled);
        Assert.Equal(BackupPolicySource.Instance, policy.EnabledSource);
    }

    // ── Rung 2: the stack's own value ────────────────────────────────────────

    [Fact]
    public void AStackValue_BeatsTheTemplateOnEveryFieldIndependently() {
        var policy = BackupPolicyResolver.Resolve(
            Stack(enabled: true, stopContainers: true, cron: "15 2 * * *", quiesceMode: BackupQuiesceMode.Stop),
            Template(enabled: false, stopContainers: false, cron: "0 4 * * *", quiesceMode: BackupQuiesceMode.Pause));

        Assert.True(policy.Enabled);
        Assert.True(policy.StopContainers);
        Assert.Equal("15 2 * * *", policy.Cron);
        Assert.Equal(BackupQuiesceMode.Stop, policy.QuiesceMode);
        Assert.Equal(BackupPolicySource.Stack, policy.EnabledSource);
        Assert.Equal(BackupPolicySource.Stack, policy.StopContainersSource);
        Assert.Equal(BackupPolicySource.Stack, policy.CronSource);
        Assert.Equal(BackupPolicySource.Stack, policy.QuiesceModeSource);
    }

    /// <summary>
    /// The four fields are independent rungs, not one decision: a tenant that only overrides the quiesce
    /// mode keeps inheriting the fleet's schedule and enrolment.
    /// </summary>
    [Fact]
    public void OneOverriddenField_DoesNotDetachTheOthersFromTheTemplate() {
        var policy = BackupPolicyResolver.Resolve(
            Stack(quiesceMode: BackupQuiesceMode.Pause),
            Template(enabled: true, stopContainers: true, cron: "0 4 * * *", quiesceMode: BackupQuiesceMode.Stop));

        Assert.Equal(BackupQuiesceMode.Pause, policy.QuiesceMode);
        Assert.Equal(BackupPolicySource.Stack, policy.QuiesceModeSource);
        Assert.True(policy.Enabled);
        Assert.Equal(BackupPolicySource.Template, policy.EnabledSource);
        Assert.Equal("0 4 * * *", policy.Cron);
        Assert.Equal(BackupPolicySource.Template, policy.CronSource);
    }

    // ── The tri-state itself ─────────────────────────────────────────────────

    /// <summary>
    /// <b>An explicit <c>false</c> is an answer, not silence.</b> A tenant opted out by hand must stay
    /// out when the fleet is switched on — collapsing null and false would silently enrol it.
    /// </summary>
    [Fact]
    public void AnExplicitFalseOnTheStack_BeatsATemplateThatSaysTrue() {
        var policy = BackupPolicyResolver.Resolve(Stack(enabled: false), Template(enabled: true));

        Assert.False(policy.Enabled);
        Assert.Equal(BackupPolicySource.Stack, policy.EnabledSource);
    }

    /// <summary>And the same in the other direction, for the field whose instance default is <c>true</c>.</summary>
    [Fact]
    public void AnExplicitFalseStopContainers_BeatsBothTheTemplateAndTheInstanceDefault() {
        Assert.False(BackupPolicyResolver.Resolve(Stack(stopContainers: false), Template(stopContainers: true))
            .StopContainers);
        Assert.False(BackupPolicyResolver.Resolve(Stack(stopContainers: false), template: null).StopContainers);
        Assert.False(BackupPolicyResolver.Resolve(Stack(), Template(stopContainers: false)).StopContainers);
    }

    /// <summary>
    /// A blank expression is silence, not an expression: a template storing <c>""</c> must not shadow the
    /// instance schedule with something that cannot be parsed.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankCron_ReadsAsInheritRatherThanAsAnExpression(string blank) {
        var stackPolicy = BackupPolicyResolver.Resolve(Stack(cron: blank), Template(cron: "0 4 * * *"));
        Assert.Equal("0 4 * * *", stackPolicy.Cron);
        Assert.Equal(BackupPolicySource.Template, stackPolicy.CronSource);

        var templatePolicy = BackupPolicyResolver.Resolve(Stack(), Template(cron: blank));
        Assert.Null(templatePolicy.Cron);
        Assert.Equal(BackupPolicySource.Instance, templatePolicy.CronSource);
    }

    /// <summary>Surrounding whitespace is trimmed, so a pasted expression resolves like a typed one.</summary>
    [Fact]
    public void ACronIsTrimmed() =>
        Assert.Equal("0 4 * * *", BackupPolicyResolver.Resolve(Stack(cron: "  0 4 * * *  "), null).Cron);

    /// <summary>The scheduler's view: "inherit" becomes the instance expression, an override stays itself.</summary>
    [Fact]
    public void EffectiveCron_FillsTheInstanceRungInFromLiveOptions() {
        var backup = new Config.BackupOptions { Cron = "30 3 * * *" };

        Assert.Equal("30 3 * * *",
            BackupPolicyResolver.EffectiveCron(BackupPolicyResolver.Resolve(Stack(), null), backup));
        Assert.Equal("0 4 * * *",
            BackupPolicyResolver.EffectiveCron(
                BackupPolicyResolver.Resolve(Stack(), Template(cron: "0 4 * * *")), backup));
    }
}
