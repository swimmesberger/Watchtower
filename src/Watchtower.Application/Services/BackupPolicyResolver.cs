using Watchtower.Application.Config;
using Watchtower.Application.Entities;

namespace Watchtower.Application.Services;

/// <summary>Which rung of the backup policy ladder an effective value came from.</summary>
/// <remarks>
/// The per-<em>service</em> ladder (<see cref="BackupSettingSource"/>) has a rung above these — the
/// compose label — because a label lives on the deployed service rather than in Watchtower's database.
/// The two enums are deliberately separate: this one answers "who set the stack's policy", that one
/// answers "who decided about this container".
/// </remarks>
public enum BackupPolicySource {
    /// <summary>Nobody set it — the instance-wide default applies.</summary>
    Instance,

    /// <summary>The stack's own template set it, and the stack inherits it.</summary>
    Template,

    /// <summary>The stack set it explicitly, overriding whatever the template says.</summary>
    Stack,
}

/// <summary>
/// One stack's effective backup policy and, per field, which rung of the ladder decided it — what the
/// scheduler, the run and the Backups tab's "Set by" labels all read.
/// </summary>
/// <param name="Enabled">Whether the schedule includes this stack.</param>
/// <param name="EnabledSource">Who decided <paramref name="Enabled"/>.</param>
/// <param name="Cron">
/// The stack's own cron expression, or null when it follows the instance schedule. Null with a
/// <see cref="BackupPolicySource.Template"/> source cannot occur: a template that says nothing leaves
/// the field at <see cref="BackupPolicySource.Instance"/>.
/// </param>
/// <param name="CronSource">Who decided <paramref name="Cron"/>.</param>
/// <param name="StopContainers">Whether the run quiesces the containers that mount archived volumes.</param>
/// <param name="StopContainersSource">Who decided <paramref name="StopContainers"/>.</param>
/// <param name="QuiesceMode">How those containers are quiesced when their service carries no label.</param>
/// <param name="QuiesceModeSource">Who decided <paramref name="QuiesceMode"/>.</param>
public sealed record BackupPolicy(
    bool Enabled,
    BackupPolicySource EnabledSource,
    string? Cron,
    BackupPolicySource CronSource,
    bool StopContainers,
    BackupPolicySource StopContainersSource,
    BackupQuiesceMode QuiesceMode,
    BackupPolicySource QuiesceModeSource);

/// <summary>
/// The one answer to "what backup policy does this stack actually run under" — the ADR-0020 ladder
/// extended by the template rung that stage 7 of ADR-0026 adds: <b>compose label &gt; stack override &gt;
/// template policy &gt; instance default</b>.
/// </summary>
/// <remarks>
/// <para>
/// The label rung is per service and lives in <see cref="BackupPlan"/>, which has always applied it; the
/// three rungs below it are per stack and live here. Every consumer of the stack-level fields goes
/// through <see cref="Resolve"/> — <see cref="Modules.Backups.BackupScheduleJob"/>,
/// <see cref="BackupService"/>'s run and preparation, and the plan preview — so a tenant cannot be
/// scheduled by one reading of the ladder and prepared by another. Reading
/// <c>Stack.BackupEnabled</c> (or any of its siblings) directly is the bug this class exists to prevent.
/// </para>
/// <para>
/// Pure and static: it takes the two rows and the options and returns a record, so each rung is
/// individually testable without a database, a template, or a Docker daemon.
/// </para>
/// </remarks>
public static class BackupPolicyResolver {
    /// <summary>
    /// The instance default for "is this stack in the schedule": <c>false</c>. Backups have always been
    /// opt-in per stack, and <c>Backup:Enabled</c> is a different thing — the master switch that turns
    /// the whole schedule off, which the scheduler checks before it looks at any stack.
    /// </summary>
    public const bool DefaultEnabled = false;

    /// <summary>The instance default for the quiesce master switch: on, as every stack had it before.</summary>
    public const bool DefaultStopContainers = true;

    /// <summary>The instance default quiesce mode: stop (application-consistent), as ADR-0019 set it.</summary>
    public const BackupQuiesceMode DefaultQuiesceMode = BackupQuiesceMode.Stop;

    /// <summary>
    /// Resolves <paramref name="stack"/>'s effective policy.
    /// </summary>
    /// <param name="stack">The stack. Its own non-null fields are the top rung below the labels.</param>
    /// <param name="template">
    /// The stack's template, or null for a standalone stack (or when the caller genuinely has none — a
    /// missing template simply means the ladder skips that rung, never that it silently reads defaults
    /// it should have inherited, which is why every caller <c>Include</c>s it).
    /// </param>
    /// <returns>The effective values with their provenance.</returns>
    public static BackupPolicy Resolve(Stack stack, StackTemplate? template) {
        var (enabled, enabledSource) = Pick(stack.BackupEnabled, template?.BackupEnabled, DefaultEnabled);
        var (stop, stopSource) =
            Pick(stack.BackupStopContainers, template?.BackupStopContainers, DefaultStopContainers);
        var (mode, modeSource) = Pick(stack.BackupQuiesceMode, template?.BackupQuiesceMode, DefaultQuiesceMode);
        // The cron's "instance default" is null — i.e. "follow Backup:Cron", which the scheduler reads
        // live. Blank is normalized to null so a template storing "" cannot shadow the instance schedule
        // with an expression that does not exist.
        var stackCron = Blank(stack.BackupCron);
        var templateCron = Blank(template?.BackupCron);
        var (cron, cronSource) = stackCron is not null ? (stackCron, BackupPolicySource.Stack)
            : templateCron is not null ? (templateCron, BackupPolicySource.Template)
            : ((string?)null, BackupPolicySource.Instance);

        return new BackupPolicy(
            enabled, enabledSource, cron, cronSource, stop, stopSource, mode, modeSource);
    }

    /// <summary>
    /// The cron expression <paramref name="policy"/> actually runs on, with the instance rung filled in
    /// from live options — the scheduler's view, where "follow the instance" has to become a real
    /// expression before it can be evaluated.
    /// </summary>
    /// <param name="policy">The stack's resolved policy.</param>
    /// <param name="backup">The instance backup options.</param>
    public static string EffectiveCron(BackupPolicy policy, BackupOptions backup) =>
        policy.Cron ?? BackupSchedule.ResolveGlobalExpression(backup);

    /// <summary>Stack value, else template value, else the instance default — with who won.</summary>
    private static (T Value, BackupPolicySource Source) Pick<T>(T? stack, T? template, T instanceDefault)
        where T : struct =>
        stack is { } own ? (own, BackupPolicySource.Stack)
        : template is { } inherited ? (inherited, BackupPolicySource.Template)
        : (instanceDefault, BackupPolicySource.Instance);

    /// <summary>Null for null, empty and whitespace; the trimmed value otherwise.</summary>
    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
