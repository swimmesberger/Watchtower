# ADR-0019: Backup quiesce — stops run per dependency level with a short grace, and `pause` is a second, crash-consistent mode

## Status

Accepted (2026-08-22). Amends [ADR-0017](0017-database-aware-dumps.md) §1 (the stop set and its
ordering); the planner, labels and dump rules of ADR-0017 are unchanged.

## Context

After ADR-0017 a stack whose only state is a Postgres is backed up with no container stopped. The
remaining downtime is the containers that mount an archived **file** volume (uploads, media, a
non-Postgres database): they were stopped with `docker stop` — SIGTERM, up to the daemon's 10 s per
container, **one after the other** — for the whole tar, then restarted from cold. For a stack with
three such services that is ≈30 s of stop latency before the tar even starts, plus the restarts.

Two observations drove the change. First, most of that time is not the tar: it is the sequential
SIGTERM grace. Second, for a file volume the thing that matters is that nothing writes to it while
the tar reads it — the application does not have to *exit* for that. The Linux cgroup freezer
(`docker pause`) suspends a container's processes in milliseconds and resumes them afterwards with
their TCP connections intact; a reader of the volume then sees exactly the bytes on disk at the
moment of the freeze.

## Decision

### 1. Stops are issued per dependency level, concurrently within a level, with a short grace

`BackupPlan` still orders the quiesce set along `depends_on` (dependents first), but now also groups
it into **levels**: a service nothing in the set depends on is level 0, its dependencies level 1, and
so on — the longest dependent path decides, so a service is never taken down before everything that
needs it. Replicas of one service share a level; containers without a compose service sit in level 0.
No `depends_on` inside the set → one level; a cycle → one container per level in engine order (the
old fallback). The executor takes a whole level down at once (`Task.WhenAll`), so the window per level
is its slowest container, not the sum; resume runs the levels backwards, concurrently within a level.
The flat order is the levels concatenated, i.e. still a valid sequential order, and restore uses the
same code.

Every stop carries `?t=N` — `Backup:StopTimeoutSeconds`, default **5 s**, clamped 1 … 300 — instead
of the daemon's 10 s. A service that needs longer than that to flush on SIGTERM is a candidate for a
dump or for `pause`, not for a longer window; the daemon's own default stays in force for every
other stop Watchtower issues (deploys, the container page).

"Resume what already went down on failure" is kept: a level that fails part-way resumes everything
taken down so far — across levels and the successful siblings of the failed one — before rethrowing.

### 2. `pause` is a second quiesce mode — crash-consistent, opt-in, never the default

A quiesced container is either **stopped** (SIGTERM, restart afterwards — the application flushes and
exits, the snapshot is application-consistent) or **paused** (`docker pause` for the tar, `unpause`
afterwards — no SIGTERM wait, no cold start, connections survive). Two controls, the same two places
as the stop decision:

- the per-service label `watchtower.backup.stop` gains the value **`pause`** beside `true`/`false`;
- the stack gains a **quiesce mode** (`Stack.BackupQuiesceMode`: `stop` | `pause`, default `stop`)
  next to the "stop stateful containers" master switch, for containers the mount rule selects that
  carry no label.

Precedence: `false` keeps running; `true` stops and `pause` pauses whatever the stack default says;
unlabelled mount-selected containers follow the stack default. **Restore always stops** (the planner's
`ForceStop`): a paused process thawed over files that were replaced underneath it is no better off
than one that kept running through the extraction — a `pause` label on restore means "quiesce it",
by stopping.

Pause is documented as **crash-consistent** and left off by default on purpose. The freezer stops
the processes, not the page cache: whatever an application still held in userspace buffers — a
database's unflushed pages, an editor's unsaved write — is not in the snapshot. That is the "pulled
the plug" state WAL/redo-log databases are designed to recover from, and it is no worse than the
`stop: false` hot copy ADR-0017 already allows — but for a database that cannot be dumped the
default stays `stop`. The honest uses are file volumes (uploads, media, generated assets) and
services whose restart is the expensive part.

### 3. Safety net: unpause in `finally`, and a persisted list reconciled on startup

A frozen container that nobody thaws is a frozen stack. Two layers, both mandatory:

- the run unpauses in `finally`, with `CancellationToken.None`, exactly as it restarts stopped
  containers — a cancelled run still thaws;
- before pausing anything, the run writes every planned pause to `backup_paused_containers`
  (container id, name, stack, timestamp); the rows are deleted once the containers are unpaused
  (a container whose unpause *failed* keeps its row). On every start the backup worker reads the table,
  inspects each container, **unpauses the ones still paused**, drops the rest (not paused any more,
  or gone), and records an audit row (`backups` / `reconcile.unpause`). The startup pass retries
  every 15 s for five minutes while the daemon is still coming up, and the check runs again before
  every job, so the gap closes even if the daemon was unreachable for the whole startup budget.

A persisted list rather than a label because Docker labels are immutable after create: there is no
way to mark a running container as "paused by Watchtower" on the container itself.

### 4. The run log and the audit row tell paused from stopped

The run log says `Quiescing N of M running container(s): stopping a; pausing b, c; leaving d up — 3
dependency levels.`, then `Pausing …`/`Stopping … (SIGTERM, 5 s grace)` per container and
`Unpaused …`/`Restarted …` on resume. The audit row's summary reads `· 2 container(s) paused, 1
stopped`; the failure path reports the setting (`containers paused` / `containers stopped`) as before.

## Rejected alternatives

- **Pause as the default.** Crash-consistent by construction; the user who turns it on should know
  what they are trading, and the database-that-cannot-be-dumped case is exactly the one a default
  would silently degrade.
- **A longer stop grace** for services that need it. The window is downtime; a service that does not
  exit in 5 s is better served by a dump or a pause.
- **Pausing on restore.** See §2 — extracting under a paused process is as unsound as under a
  running one.
- **Marking paused containers with a label.** Not possible on a running container.
- **One pause row per container, written inside the per-container task.** Parallel writers into
  SQLite for a few rows is asking for `database is locked` inside the stop window; one batch write
  before the window costs nothing and carries the same information.

## Consequences

- A stack of file-volume services is quiesced for roughly the tar duration: the stop grace is gone
  for paused services and bounded at 5 s (per level, not per container) for stopped ones.
- `pause` snapshots are crash-consistent; the docs and the UI say so in those words.
- A new table and a new `stacks` column (`backup_quiesce_mode`, backfilled `Stop`); the DTO
  `BackupStackConfigDto` gains `quiesceMode` (`"stop"` | `"pause"`), the command accepts it
  optionally (null reads as `stop`).
- The stop order within a level is no longer the engine's order but concurrent; a stack that relied
  on an *undeclared* ordering between two services now has to declare it with `depends_on`.
