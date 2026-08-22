using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Elarion.Abstractions.Scheduling;
using Watchtower.Application.Config;

namespace Watchtower.Application.Services;

/// <summary>
/// The pure half of the backup schedule (ADR-0018): which cron expression applies to a stack, whether
/// it is valid, what it means in words, and — given the clock and the stack's last scheduled run —
/// whether a window is due right now. No host, no database; <see cref="Modules.Backups.BackupScheduleJob"/>
/// is the minute tick that feeds it.
/// </summary>
/// <remarks>
/// Expressions are the classic five-field Unix form (<c>minute hour day-of-month month day-of-week</c>)
/// and are evaluated against the server-local wall clock — the same semantics <c>Backup:Time</c> always
/// had. Misfire policy: a window the tick notices late (restart, downtime, master switch off, stack
/// just opted in, schedule just changed) is run once if it is younger than the configured grace, and
/// skipped otherwise; only the <em>latest</em> late window is ever run, never a burst.
/// </remarks>
public static class BackupSchedule {
    /// <summary>The schedule when nothing is configured: 03:30 every day, as before cron.</summary>
    public const string DefaultExpression = "30 3 * * *";

    /// <summary>How often the schedule is evaluated; also the floor of the misfire grace.</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Grace bounds: at least two ticks, so a window is never missed by the tick's own jitter; at most
    /// a day, which also bounds the work a dense expression (<c>* * * * *</c>) costs per evaluation.
    /// </summary>
    public static readonly TimeSpan MinimumMisfireGrace = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan MaximumMisfireGrace = TimeSpan.FromHours(24);

    /// <summary>
    /// The instance-wide expression: <c>Backup:Cron</c> when set, else the legacy <c>Backup:Time</c>
    /// alias (<c>HH:mm</c> → <c>M H * * *</c>), else <see cref="DefaultExpression"/>. The result may
    /// still be invalid (a bad env var) — callers parse it with <see cref="TryParse"/>.
    /// </summary>
    public static string ResolveGlobalExpression(BackupOptions backup) {
        if (!string.IsNullOrWhiteSpace(backup.Cron)) return backup.Cron.Trim();
        if (TryFromTimeOfDay(backup.Time, out var alias)) return alias;
        return DefaultExpression;
    }

    /// <summary>The misfire grace from the options, clamped to the supported bounds.</summary>
    public static TimeSpan ResolveMisfireGrace(BackupOptions backup) {
        var grace = TimeSpan.FromMinutes(backup.MisfireGraceMinutes);
        if (grace < MinimumMisfireGrace) return MinimumMisfireGrace;
        return grace > MaximumMisfireGrace ? MaximumMisfireGrace : grace;
    }

    /// <summary>The expression a stack runs on: its own override when set, else the instance-wide one.</summary>
    public static string Effective(string? stackOverride, string globalExpression) =>
        string.IsNullOrWhiteSpace(stackOverride) ? globalExpression : stackOverride.Trim();

    /// <summary>Translates the legacy <c>HH:mm</c> setting into its cron equivalent (<c>M H * * *</c>).</summary>
    public static bool TryFromTimeOfDay(string? time, [NotNullWhen(true)] out string? expression) {
        expression = null;
        if (string.IsNullOrWhiteSpace(time)) return false;
        if (!TimeOnly.TryParseExact(time.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            return false;
        expression = $"{t.Minute} {t.Hour} * * *";
        return true;
    }

    /// <summary>
    /// Validates and parses a schedule expression. Exactly five fields are accepted — second-level
    /// schedules are meaningless for a minute tick — and the expression must actually produce an
    /// occurrence. <paramref name="error"/> is phrased for the operator (it is the validation message).
    /// </summary>
    public static bool TryParse(
        string? expression,
        [NotNullWhen(true)] out CronExpression? cron,
        [NotNullWhen(false)] out string? error) {
        cron = null;
        error = null;
        var text = expression?.Trim() ?? "";
        if (text.Length == 0) {
            error = "Schedule must be a cron expression with five fields: minute hour day-of-month month day-of-week (e.g. \"30 3,15 * * *\").";
            return false;
        }
        var fields = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) {
            error = $"Schedule \"{text}\" must have exactly five fields: minute hour day-of-month month day-of-week (e.g. \"30 3,15 * * *\").";
            return false;
        }
        try {
            var parsed = CronExpression.Parse(text);
            // Some syntactically valid expressions never match (e.g. "0 0 31 2 *"); Elarion reports that
            // lazily from GetNextOccurrence, so probe once here rather than at 03:30.
            _ = parsed.GetNextOccurrence(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);
            cron = parsed;
            return true;
        } catch (FormatException ex) {
            error = $"Schedule \"{text}\" is not a valid cron expression: {ex.Message}";
            return false;
        } catch (InvalidOperationException) {
            error = $"Schedule \"{text}\" never occurs.";
            return false;
        }
    }

    /// <summary>
    /// Decides what the tick should do for one stack at <paramref name="now"/>.
    /// </summary>
    /// <param name="cron">The stack's effective expression.</param>
    /// <param name="now">The current instant.</param>
    /// <param name="lastScheduledAt">Due time of the last window the scheduler ran for the stack; null if none yet.</param>
    /// <param name="misfireGrace">How old a window may be and still be run (clamped to the supported bounds).</param>
    /// <param name="timeZone">The wall clock the expression is evaluated in (server-local in production).</param>
    /// <returns>
    /// <c>DueAt</c> is the window to run now (the latest occurrence within the grace that is newer than
    /// <paramref name="lastScheduledAt"/>), or null. <c>MissedAt</c> is the first window after
    /// <paramref name="lastScheduledAt"/> that fell outside the grace and is therefore skipped, for the
    /// log — null when nothing was missed or there is no history to compare against.
    /// </returns>
    public static ScheduleDecision Evaluate(
        CronExpression cron,
        DateTimeOffset now,
        DateTimeOffset? lastScheduledAt,
        TimeSpan misfireGrace,
        TimeZoneInfo timeZone) {
        if (misfireGrace < MinimumMisfireGrace) misfireGrace = MinimumMisfireGrace;
        if (misfireGrace > MaximumMisfireGrace) misfireGrace = MaximumMisfireGrace;
        var windowStart = now - misfireGrace;

        // Latest occurrence in (windowStart, now] — the only one that may still run.
        DateTimeOffset? due = null;
        var candidate = cron.GetNextOccurrence(windowStart, timeZone);
        while (candidate <= now) {
            due = candidate;
            candidate = cron.GetNextOccurrence(candidate, timeZone);
        }
        if (due is not null && lastScheduledAt is not null && due <= lastScheduledAt)
            due = null; // already ran (or the schedule moved backwards) — never fire a window twice

        // Anything between the last run and the grace window is gone for good; report the first one.
        DateTimeOffset? missed = null;
        if (lastScheduledAt is not null) {
            var nextAfterLast = cron.GetNextOccurrence(lastScheduledAt.Value, timeZone);
            if (nextAfterLast <= windowStart) missed = nextAfterLast;
        }

        return new ScheduleDecision(due, missed);
    }

    /// <summary>
    /// Puts a five-field expression into words for the UI preview and the audit trail — e.g.
    /// <c>every day at 03:30 and 15:30</c>, <c>every 6 hours at :00</c>, <c>on Mon, Wed and Fri at
    /// 02:00</c>. Shapes the describer does not recognise fall back to <c>cron "…"</c>, so the text is
    /// always truthful, if not always pretty. The frontend mirrors these rules for its live preview.
    /// </summary>
    public static string Describe(string expression) {
        var raw = expression.Trim();
        var fallback = $"cron \"{raw}\"";
        var fields = raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5) return fallback;
        if (!CronFieldExpander.TryExpand(fields[0], 0, 59, null, out var minutes)
            || !CronFieldExpander.TryExpand(fields[1], 0, 23, null, out var hours)
            || !CronFieldExpander.TryExpand(fields[2], 1, 31, null, out var daysOfMonth)
            || !CronFieldExpander.TryExpand(fields[3], 1, 12, CronFieldExpander.MonthNames, out var months)
            || !CronFieldExpander.TryExpand(fields[4], 0, 7, CronFieldExpander.DayNames, out var daysOfWeek))
            return fallback;
        if (daysOfWeek.Remove(7)) daysOfWeek.Add(0); // 7 is Sunday too

        var anyDom = CronFieldExpander.IsWildcard(fields[2]);
        var anyMonth = CronFieldExpander.IsWildcard(fields[3]);
        var anyDow = CronFieldExpander.IsWildcard(fields[4]);
        // Unix semantics when both day fields are restricted (either matches) read badly in prose — punt.
        if (!anyMonth || (!anyDom && !anyDow)) return fallback;

        string when;
        if (anyDom && anyDow) when = "every day";
        else if (!anyDow) when = DescribeDaysOfWeek(daysOfWeek) ?? fallback;
        else when = $"on day {JoinList(daysOfMonth.Order().Select(d => d.ToString(CultureInfo.InvariantCulture)))} of every month";
        if (when == fallback) return fallback;

        var time = DescribeTimes(minutes, hours, fields[0], fields[1]);
        if (time is null) return fallback;
        // "every day every 6 hours" / "every day 9 times a day" say nothing the time part does not.
        var timeImpliesEveryDay = time.StartsWith("every ", StringComparison.Ordinal) || time.EndsWith(" a day", StringComparison.Ordinal);
        return when == "every day" && timeImpliesEveryDay ? time : $"{when} {time}";
    }

    private static string? DescribeTimes(HashSet<int> minutes, HashSet<int> hours, string minuteField, string hourField) {
        var allMinutes = minutes.Count == 60;
        var allHours = hours.Count == 24;
        if (allMinutes && allHours) return "every minute";
        if (allMinutes) return null;
        if (minutes.Count == 1) {
            var minute = minutes.First();
            if (allHours) return $"every hour at :{minute:00}";
            if (CronFieldExpander.TryStep(hourField, 0, out var step) && step > 1)
                return $"every {step} hours at :{minute:00}";
        }
        if (CronFieldExpander.TryStep(minuteField, 0, out var minuteStep) && minuteStep > 1 && allHours)
            return $"every {minuteStep} minutes";

        var times = hours.Order()
            .SelectMany(h => minutes.Order().Select(m => $"{h:00}:{m:00}"))
            .ToList();
        if (times.Count > 8) return $"{times.Count} times a day";
        return $"at {JoinList(times)}";
    }

    private static string? DescribeDaysOfWeek(HashSet<int> days) {
        if (days.Count == 0) return null;
        if (days.SetEquals([1, 2, 3, 4, 5])) return "on weekdays";
        if (days.SetEquals([0, 6])) return "on weekends";
        if (days.Count == 7) return "every day";
        var names = days.Order().Select(d => CronFieldExpander.DayLabels[d]);
        return $"on {JoinList(names)}";
    }

    /// <summary>"a", "a and b", "a, b and c".</summary>
    private static string JoinList(IEnumerable<string> items) {
        var list = items.ToList();
        return list.Count switch {
            0 => "",
            1 => list[0],
            2 => $"{list[0]} and {list[1]}",
            _ => $"{string.Join(", ", list.Take(list.Count - 1))} and {list[^1]}",
        };
    }

    /// <summary>
    /// A minimal cron field expander for the describer only — Elarion's <see cref="CronExpression"/>
    /// is the authority on validity and matching; this just needs the value set to put into words.
    /// </summary>
    private static class CronFieldExpander {
        public static readonly string[] MonthNames =
            ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

        public static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

        public static readonly string[] DayLabels = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];

        public static bool IsWildcard(string field) => field is "*" or "?";

        /// <summary>True for <c>*/n</c> or <c>start-end/n</c> starting at <paramref name="min"/>; yields n.</summary>
        public static bool TryStep(string field, int min, out int step) {
            step = 0;
            var slash = field.IndexOf('/');
            if (slash < 0) return false;
            var range = field[..slash];
            if (!(range == "*" || range == min.ToString(CultureInfo.InvariantCulture) || range.StartsWith($"{min}-", StringComparison.Ordinal)))
                return false;
            return int.TryParse(field[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) && step > 0;
        }

        public static bool TryExpand(string field, int min, int max, string[]? names, out HashSet<int> values) {
            values = [];
            foreach (var part in field.Split(',')) {
                if (part.Length == 0) return false;
                var step = 1;
                var range = part;
                var slash = part.IndexOf('/');
                if (slash >= 0) {
                    if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0)
                        return false;
                    range = part[..slash];
                }
                int start, end;
                if (range is "*" or "?") {
                    start = min;
                    end = max;
                } else {
                    var dash = range.IndexOf('-');
                    if (dash >= 0) {
                        if (!TryValue(range[..dash], min, max, names, out start) || !TryValue(range[(dash + 1)..], min, max, names, out end))
                            return false;
                    } else {
                        if (!TryValue(range, min, max, names, out start)) return false;
                        end = slash >= 0 ? max : start;
                    }
                }
                if (start > end) return false;
                for (var v = start; v <= end; v += step) values.Add(v);
            }
            return values.Count > 0;
        }

        private static bool TryValue(string text, int min, int max, string[]? names, out int value) {
            if (names is not null) {
                var index = Array.FindIndex(names, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) {
                    value = min + index;
                    return true;
                }
            }
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
        }
    }
}

/// <summary>What the schedule tick should do for one stack — see <see cref="BackupSchedule.Evaluate"/>.</summary>
public readonly record struct ScheduleDecision(DateTimeOffset? DueAt, DateTimeOffset? MissedAt);
