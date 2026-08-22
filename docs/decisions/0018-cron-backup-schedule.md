# ADR-0018: Cron-based backup schedule with per-stack overrides, on the Elarion scheduler

## Status

Accepted (2026-08-22). Amends [ADR-0016](0016-stack-backups.md) §6.

## Context

ADR-0016 gave backups exactly one window per day: a global `Backup:Time` (`HH:mm`, server-local)
checked by a hand-rolled one-minute `BackgroundService` with an in-memory "already fired today"
flag. Operators want more than one run per day (03:30 *and* 15:30, or every six hours), and since
[ADR-0017](0017-database-aware-dumps.md) scoped the stop window to the containers that mount an
archived volume — and removed it entirely for Postgres — the downtime argument against frequent
backups is gone.

Two things about the old design had to go with it. The in-memory flag meant a restart could not tell
a window it had already fired from one it had slept through, so it baselined silently and a window
that passed during a restart was simply lost. And Watchtower was not using Elarion's scheduler at
all, although the pinned packages ship one (`[ScheduledJob]`, `CronExpression`, overlap/misfire
policies — see the project notes in `docs/reverse-proxy/elarion-framework-notes.md` §A2 on the
hand-rolled polling services being adoption debt).

## Decision

### 1. The schedule is a five-field cron expression, instance-wide with a per-stack override

`Backup:Cron` (`minute hour day-of-month month day-of-week`, classic Unix syntax as Elarion's
`CronExpression` parses it, **exactly five fields** — seconds make no sense for a minute tick) is the
instance schedule; default `30 3 * * *`, i.e. what `03:30` meant before. `Stack.BackupCron` (nullable)
replaces it for one stack. `Backup:Enabled` stays the master switch and `Stack.BackupEnabled` the
per-stack opt-in — an override is a *schedule*, not an opt-in.

`Backup:Time` survives as a compatibility alias: a non-blank `HH:mm` reads as `M H * * *`, so an
existing `WATCHTOWER__BACKUP__TIME` keeps working unchanged. Precedence is `Cron` → `Time` alias →
default. Saving a schedule from the UI writes `Backup:Cron` and removes a stored `Backup:Time`, so the
alias cannot keep feeding the old time into the fallback chain; an env-pinned `Backup:Time` pins the
schedule field exactly as a pinned `Backup:Cron` would (ADR-0014), because it is where the effective
expression comes from while it is set.

Expressions are evaluated against the **server-local wall clock** (`TimeZoneInfo.Local`), as
`Backup:Time` always was and as `AutoDeployTime` still is. Nothing forced a change, and a schedule
that reads "03:30" should mean the host's 03:30.

### 2. One Elarion `[ScheduledJob]` minute tick evaluates every stack; the cursor is persisted

Per-stack expressions live in the database and change at runtime, so they cannot be registered as
individual scheduler jobs (Elarion's descriptors are compile-time). The one recurring job that can be
— `BackupScheduleJob`, `FixedRate = "1m"`, `Overlap = Skip`, registered with the Backups module by
`AddElarion` and run by the host's `AddElarionScheduler` — evaluates each opted-in stack's effective
expression with `BackupSchedule.Evaluate` and enqueues the due ones on the single-flight backup queue
(ADR-0016 §6, unchanged). This is Watchtower's first use of the framework scheduler; the remaining
hand-rolled polling services are unchanged and remain a separate, deliberate migration.

`Stack.LastScheduledBackupAt` is the scheduler's cursor: the due time of the last window it enqueued
for the stack, written in the same save as the enqueue. It is what makes a restart safe — the tick
compares windows against a persisted instant, not against process memory. Manual runs never move
it. The migration seeds it from the newest schedule-triggered `BackupEvent` per stack, so an upgrade
shortly after the day's window does not run it a second time.

### 3. Misfire policy: the latest late window runs once within a grace, older windows are skipped

When the tick sees a window late — after a restart or downtime, with the master switch having been
off, with the stack having just opted in, or with the schedule having just been changed — it runs
**the latest** missed window once if that window is younger than `Backup:MisfireGraceMinutes`
(default 60, clamped to 2 minutes … 24 hours), and skips everything older, logging the first skipped
window once. Never a burst: ten missed six-hourly windows become at most one run. This is the same
shape as Elarion's own `FireOnce` policy with a bound on how late "once" may be, chosen over plain
`FireOnce` (which would run a 20-hour-old window) and over `Skip` (which would lose a daily backup to
a two-minute restart).

The grace applies uniformly — there is no separate "baseline silently" rule for enable/opt-in like
the old service had. A stack opted in, or the master switch turned on, shortly after a window *runs*
that window once; the run shows up as `queued` in the tab immediately, which doubles as confirmation
that the schedule works, and the cost of one backup is small now that stops are scoped (ADR-0017).

### 4. Retention with several runs a day

`BackupRetention` already orders by the file name's second-resolution UTC timestamp, so several runs
per day are distinct archives and `RetentionMaxCount` counts *runs*, not days. With an age limit
alone, two runs a day for 30 days keep 60 archives; the UI hint and `docs/backups.md` now say to set
the count limit alongside the schedule.

## Consequences

- `backups.getConfig`/`updateConfig` exchange `cron` instead of `time`; `backups.getStackConfig`/
  `setStackConfig` carry the nullable `cron` override. Both validate through `BackupSchedule.TryParse`
  and fail with `AppError.Validation` and an operator-readable message. Audit rows spell the schedule
  out (`schedule on, 30 3,15 * * * (every day at 03:30 and 15:30)`); the frontend preview uses the
  same describer rules (`src/lib/cron.ts` mirrors `BackupSchedule.Describe`).
- The host now runs Elarion's scheduler (`Scheduler:*` configuration applies; defaults are fine).
  Scheduled-job descriptors are composed per module by `AddElarion`, so a future second job is an
  attribute away.
- `WATCHTOWER__BACKUP__TIME` is legacy: documented, honoured, and reported as pinning the schedule.
  Operators should move to `WATCHTOWER__BACKUP__CRON`; the alias may be dropped in a later major.
- Anything other than a five-field expression is rejected at the boundary, including Elarion's own
  six-field (seconds) form and the `-` "disabled" sentinel — disabling is the master switch's job.
