# ADR-0020: Per-service backup settings — labels win, UI overrides fill the gaps, and the plan preview shows the effective result

## Status

Accepted (2026-08-22). Builds on [ADR-0017](0017-database-aware-dumps.md) (the labels) and
[ADR-0019](0019-pause-quiesce-and-parallel-stops.md) (`pause`); applies
[ADR-0014](0014-env-wins-runtime-settings.md)'s precedence rule to compose labels.

## Context

Per-service backup behaviour — exclude a service, stop/pause/keep it, dump or not — existed only as
compose labels (`watchtower.backup.exclude|stop|dump`). Labels are the right *infrastructure-as-code*
surface: versioned with the stack, next to the service they describe, and the only control that
survives registering the stack in a fresh Watchtower. But they were invisible from the UI: nothing
showed which labels a deployed stack carried, what the next run would do with each container, or
that the labels existed at all unless one read `docs/backups.md`. Watchtower is meant to be usable
both ways — click-through in the UI and declaratively through labels — and the question was which
wins when both say something, and how the UI makes the label way discoverable.

The house precedent is ADR-0014: environment variables (IaC) win over runtime settings, and the UI
renders a pinned setting read-only with the variable's name, rather than accepting an edit that would
silently never take effect.

## Decision

### 1. Precedence: label → UI override → stack default → mount rule

Per service and **per knob** (the three labels are independent settings), highest first:

1. the compose label on the deployed service — read-only in the UI, shown with its text;
2. a per-service **UI override** (`stack_backup_service_overrides`: stack, service, `exclude`,
   `stop`, `dump`) — stored in the labels' own value syntax, one row per (stack, service), replaced
   whole on save, deleted when every knob is cleared;
3. the stack default (the "stop stateful containers" switch and the quiesce mode);
4. the mount rule.

The planner reads the *effective* value through `BackupContainer.Exclude/Stop/Dump`
(`BackupSetting`: value + `BackupSettingSource` = `Label | Override | Default`), and every decision
it emits — `BackupQuiesceStep`, `KeptBackupContainer` — carries that source. The dump policy reads
the same effective values. An override that a label currently shadows is kept, not refused: labels
come and go with deploys, and the preview says which one is in effect.

Why labels on top: a UI override that silently beat a label would make the compose file lie to the
next person who reads it — exactly the drift ADR-0014 refuses for env vars. Why the UI can still
write an override for a labelled service's *other* knobs: the knobs are independent, and locking a
whole service because one label exists would force the operator into YAML for a setting that has no
label yet.

### 2. The plan preview is the discoverability surface

`backups.previewPlan` runs the *same* preparation as a backup (`BackupService.PrepareAsync`:
volumes, containers, dump targets, plan — one code path, so the preview cannot drift from the run)
and returns one row per container: service, state, mounted volumes, what the next run does (`stop`
/ `pause` / `keep` / `dump` / `excluded` / `notRunning`), prose why, the source, the raw labels, and
the override. Plus the planner's warnings (a typo'd label is visible before 03:30, not in a run
nobody watches) and the archived/excluded volumes. The Backups tab renders it as a table: the label
shows with a lock icon as the read-only source, a per-row menu sets the override for unlabelled
knobs (the labelled knob is disabled with "set by label"), and an override for a service that is not
deployed right now is listed as `absent` so it can be found and removed.

### 3. Overrides promote to labels

The preview carries a **compose snippet** (`ComposeLabelSnippet.Render`) — the stack's overrides as
`services: <name>: labels: watchtower.backup.x: "y"` — byte-for-byte the values the planner reads
back, so pasting the snippet into the compose file and clearing the overrides changes nothing about
the next run. That is the bridge from "configured here" to "versioned with the stack": the UI is
where one experiments and discovers; the labels are where the result belongs once it is settled.

## Rejected alternatives

- **UI wins over labels.** No visible source of truth; contradicts ADR-0014; the compose file misleads.
- **Last writer wins.** Same problem, with the added mystery of ordering.
- **Labels only, better documented.** The user's point: a setting nobody can see in the product is
  not discoverable; documentation is not a surface.
- **A whole-service lock when any label exists.** Forces YAML for knobs that have no label yet.
- **Computing the preview from the compose file.** The run reads the *deployed* containers' labels
  (ADR-0017), so the preview must too, or it would describe a stack that is not running.

## Consequences

- New table `stack_backup_service_overrides`; two RPCs (`backups.previewPlan`,
  `backups.setServiceOverride`); `BackupStackConfigDto` unchanged.
- Each preview lists volumes and containers and inspects the dump candidates — a few engine calls per
  open of the Backups tab, never a stop.
- Restore ignores overrides' `pause` exactly as it ignores the label's (`ForceStop`, ADR-0019); the
  exclude/dump knobs apply to restore as before.
- The dump policy's log line names the source (`UI override dump=false` vs the label), so the run
  log reads the same way the preview does.
