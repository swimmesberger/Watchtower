# ADR-0025: Stacks can be stopped and started as a whole, and the stop is a persisted desired state

## Status

Accepted (2026-08-24).

## Context

Every lifecycle operation Watchtower offered was either per-container (`containers.stop`,
`containers.restart`) or destructive at the stack level (delete, tenant teardown via
`compose down`). There was no way to "disable" a stack that is not currently in use: stop all of
its containers at once, keep its configuration, volumes and containers intact, and have that state
*stick* — a stack an operator stopped by hand today comes back on the next webhook push, the next
auto-deploy tick, or (with `restart: always`) the next Docker daemon restart.

Watchtower itself never starts containers outside a deploy — stacks survive host reboots through
Docker restart policies, not through any boot-time "bring everything up" pass. So keeping a stack
down is not about suppressing a startup routine; it is about (a) remembering the intent across
Watchtower restarts and (b) winning against the two actors that revive containers: Watchtower's own
deploy paths, and Docker's restart policies.

## Decision

### 1. Desired state is a column on `stacks`, not a runtime flag

`Stack.DesiredState` (`Running` | `Stopped`, stored as the enum name with a `Running` model default,
like `BackupQuiesceMode`) records the operator's intent. Observed container state stays what it
always was — read live from Docker; the column only says what the operator *wants* (ADR-0024: state
belongs in the database, so a Watchtower restart or reinstall changes nothing).

### 2. `stacks.stop` / `stacks.start` operate on the compose project by name

`stacks.stop` sets the desired state and runs `docker compose --project-name <p> stop`;
`stacks.start` sets it back and runs `… start`. Like tenant teardown's `DownProjectAsync`, there is
no repository checkout outside a deploy to point `--file` at, so the project resolves from the
`com.docker.compose.project` labels on its containers. `stop`/`start` — not `down`/`up` — because
containers, networks and anonymous volumes are kept: starting a disabled stack is fast and loses
nothing, and no clone/pull/recreate happens outside a deploy. A stack whose containers were never
created (or were removed) is re-enabled without a compose call — `compose start` errors on an empty
project — and the operator deploys to create them; the RPC response says so (`started: false`).

The two handlers order intent and action oppositely, each so that the crash window between the two
steps is the benign one. `stacks.stop` stops first and persists after: a `Stopped` row over
containers a failed stop left running would make the startup reconcile "finish" a stop that never
happened. `stacks.start` persists first and starts after: were the intent still `Stopped` while
containers come up, a crash in between would leave a stack the reconcile then re-stops against the
operator's wishes — whereas its converse, a re-enabled stack whose containers are still down, is
answered by retrying or deploying.

### 3. Every deploy path refuses a stopped stack

A deploy of a stopped stack would rebuild and start its containers, silently undoing the stop. So
the intent has to be reversed explicitly first — `stacks.deploy` and the webhook answer 409, the
auto-deploy tick skips stopped stacks, and `DeployQueueService.ExecuteDeployAsync` fails the run as
a backstop for deploys already queued when the stop landed (or racing it). The webhook checks after
bearer auth, so only an authorized caller learns the state.

### 4. A startup reconcile re-stops what Docker revived

`docker stop` does not survive the daemon: a container with `restart: always` is revived when the
daemon restarts (host reboot, engine upgrade). Following the ADR-0019 §3 pattern — persisted intent
in the database plus a reconcile at process start, a label being unusable for the same reason (it
would require recreating the container) — `StackDesiredStateReconciler` runs once per start: for
every `Stopped` stack with a container `running` or `restarting`, it re-runs the project stop, logs
a warning and writes one `stacks`/`reconcile.stop` audit row. Like the backup unpause reconcile it
retries for a few minutes, because on a host reboot the daemon regularly comes up after Watchtower —
the very moment the reconcile matters.

The reconcile deliberately runs only at startup. Continuously enforcing the state would need another
polling loop (ADR-0018 calls those adoption debt), and the window it would close — a daemon restart
while Watchtower keeps running — also reopens the moment an operator uses `docker start` directly,
which is a hand they may deliberately play (debugging one container of a disabled stack).

## Consequences

- `stacks` gains a backfilled `desired_state` column; `StackDto` exposes it as
  `desiredState: "running" | "stopped"` and the UI shows stop/start controls and a stopped badge.
- A stopped stack's webhook returns 409 to CI — pushes made while a stack is disabled are *not*
  queued for later; the operator deploys (or pushes again) after starting it.
- Backups of a stopped stack keep working: the quiesce set is built from running containers, so
  there is nothing to stop and nothing gets restarted afterwards — but database dumps (ADR-0017)
  need a running Postgres and will fail; disabling backups for a long-term-stopped stack is the
  operator's move.
- `containers.stop`/`containers.restart`/`docker` CLI still act on single containers of any stack —
  desired state constrains Watchtower's own automation, not the operator's hands.
