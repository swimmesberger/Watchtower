# ADR-0017: Database-aware backups — Postgres dumps replace the data-volume snapshot; stops are scoped to the volumes being archived

## Status

Accepted

## Context

ADR-0016 made a stack backup a file-level archive of the stack's compose volumes, with a per-stack
"stop containers during the snapshot" switch (default on) as the consistency model: a tar of a
live database volume is read over a time window and is therefore worse than a crash snapshot. It
named application-aware dumps as out of scope, with compose labels as the extension point.

Three things about that model hurt in practice:

- The stop set was the **whole compose project**. For the common shape — stateless web and api
  containers plus one Postgres — everything went down, although only the database owns state.
- Stop and restart ran in the Docker listing order (newest first), with no knowledge of
  `depends_on`: an api could come back before its database.
- Even a scoped stop is downtime for the database, and a logical dump needs none. Postgres is also
  the one engine whose official image makes a dump trivially reachable from outside: `pg_dumpall`
  and `psql` ship in the image, and the image's `pg_hba.conf` trusts local socket connections, so
  `docker exec db pg_dumpall -U postgres` works with no credential plumbing.

The constraints from ADR-0016 still hold: Watchtower's only privilege is the Docker socket, the
shipped image gains no binaries, and the storage/encryption format of ADR-0016 stays readable by
stock tools on a host without Watchtower.

## Decision

### 1. The stop set is scoped to the volumes being archived, honours two compose labels, and follows `depends_on`

A pure planner (`BackupPlan.Create`) takes the project's containers (every state, with their
labels and named-volume mounts) and the candidate volumes, and returns the final volume set and an
ordered stop list. Rules, first match wins per running container: master switch off → keep;
`watchtower.backup.exclude=true` → keep; caller asked to keep (a database being dumped) → keep;
`watchtower.backup.stop=false` → keep; `watchtower.backup.stop=true` → stop; mounts at least one
volume of the final set → stop; otherwise keep.

Volumes: a volume is excluded from the archive only when **every** container that mounts it carries
`watchtower.backup.exclude=true` — a volume shared with a non-excluded service is still archived
(with a warning), because silently dropping the other service's data is the worse failure. A volume
no container mounts is archived as before.

Ordering: services are sorted by `com.docker.compose.depends_on` (Kahn, deterministic
tie-breaking); dependents stop before their dependencies and restart after them; replicas stop
highest-numbered first. Missing labels fall back to the engine's order unchanged; a cycle logs a
warning and falls back for the whole run rather than ordering half a stack. The labels are read
from the live containers, so no compose file is needed at backup time — the same holds on restore,
where only containers that mount a restored volume are stopped.

### 2. Postgres containers are detected by image and dumped with `pg_dumpall`; the dump replaces the data-volume snapshot

Detection is an **exact match on the image repository's last path segment** against a short list
(`postgres`, `postgresql`, `postgis`, `pgvector`, `timescaledb`, `timescaledb-ha`,
`pgautoupgrade`), never a substring test: `postgrest/postgrest` and
`prometheuscommunity/postgres-exporter` both contain "postgres", and a false positive would exec
`pg_dumpall` in a REST server and *skip the volume snapshot it mounts*. The label
`watchtower.backup.dump` is the escape hatch in both directions: `false` (also `0`, `off`, `no`)
forces the ADR-0016 volume snapshot, `postgres` marks an unlisted image as a target.

A detected, running container is not stopped. Before anything is stopped, a preflight proves the
cluster answers (`psql -tAc "select 1"`, retried once as the `postgres` OS user for peer-auth
setups; `POSTGRES_USER`/`POSTGRES_PASSWORD` are read from the container's environment and handed
back only as exec environment, never logged). The dump runs **inside the stop window** — after the
stack's other stateful containers are stopped — so the dump and the file snapshot describe one
logical state. The command is `pg_dumpall --clean --if-exists --no-password`: a whole-cluster SQL
dump (roles, passwords, all databases), because it replaces a volume that held the whole cluster.
Role passwords are kept; the dump is exactly as sensitive as the `PGDATA` it replaces, which is
what the encryption passphrase of ADR-0016 §4 is for.

The volume mounted at `PGDATA` (the `PGDATA` environment variable, else
`/var/lib/postgresql/data`, or `/bitnami/postgresql`) is excluded from the archive; any other
volume the database container mounts is archived as before. A database on a bind mount or
anonymous volume (not backed up by ADR-0016 at all) gains a backup through the dump without a
special case.

A non-zero `pg_dumpall` exit **fails the run**. By then the plan is fixed — the data volume was
excluded and the database deliberately kept running — so a fallback snapshot would be a hot copy of
a live `PGDATA`, produced silently: an archive that quietly lacks its database is the worst failure
a backup system has. The previous archive is still on the storage. Detection-time problems (label
opt-out, container not running) fall back to the volume snapshot with a warning, because there the
plan is not yet fixed.

### 3. Exec goes through the Engine API, and dumps travel as files, never via stdin

`DockerEngineClient` gains exec create/start/inspect. Without stdin an exec start is a plain
streamed HTTP response (the same multiplexed framing the log stream already consumes), so the
connection-hijacking part of the exec API is never touched and the whole path is testable against
the HTTP-layer fakes. The dump's stdout streams into a temp file; the restore copies the SQL into
the container with the archive PUT endpoint and runs `psql -f` on it. Shelling out to the bundled
`docker exec` CLI was rejected: it is untestable without a daemon and needs a concurrent stderr
drain to avoid pipe deadlocks, for no capability the API lacks.

### 4. The archive gains `backup/_dumps/{service}.sql`; the manifest becomes `formatVersion: 2` only when dumps are present

Dump files are injected into the helper container next to the manifest (one archive PUT at `/`
carrying `backup/backup-manifest.json` and `backup/_dumps/…`), so the tar remains a single stream a
stock `tar` reads end to end. Concatenating a second tar onto the daemon's stream was rejected: tar
readers stop at the first end-of-archive blocks, so the appended part would be invisible to exactly
the tools the manual restore documents. The manifest lists each dump (`service`, `engine`, `file`,
`image`, `user`, `container`, the `volumes` it covers, the `databases` it contained, `sizeBytes`).
`formatVersion` stays `1` for a stack without dumps — a reader's required understanding, not the
writer's age — so archives of stacks without Postgres are byte-identical to before.

### 5. Restore replays the dump into the running database, with the rest of the stack down

The restore scans the archive, matches dump files against the manifest and against the host's
containers **before touching anything** (a manifest entry without its file, or no Postgres
container for the service, refuses the restore). Volumes restore as in ADR-0016, with the data
volume left in place. Then, with the stack's other containers still stopped: stray sessions are
terminated (`--clean` cannot `DROP DATABASE` under a live connection and would merge into the old
database instead), the SQL is copied in and replayed with
`psql -v ON_ERROR_STOP=0 -f`, and success is judged by the **presence of every database the
manifest lists**, not by psql's exit code: `pg_dumpall --clean` output reliably errors on
`role "postgres" already exists`, so `ON_ERROR_STOP=1` would abort every restore. Diagnostics are
counted and reported as a warning; missing databases fail the run.

## Rejected alternatives

- `Contains("postgres")` detection — see §2.
- Dump before the stop window — shorter outage, but an archive whose volumes are newer than its
  database; a self-inconsistent backup is worse than a longer 03:30 window.
- `--no-role-passwords` — leaves every application login broken after a restore.
- `ON_ERROR_STOP=1` / `--single-transaction` — the former breaks on `--clean`'s benign errors, the
  latter is impossible across `CREATE DATABASE` and `\connect`.
- Tar concatenation and the `docker exec` CLI — §3, §4.
- A stack-level "dump databases" toggle — the per-service label is the control the user asked for;
  a global switch would add a second, partially wired config surface.

## Consequences

- Down-time is proportional to the stateful part of the stack; a stack whose only state is a
  Postgres is backed up with no container stopped.
- A dump failure fails the run loudly; operators see it in the history and the audit trail.
- Dumps are plain SQL (no parallel restore, no selective restore); gzip downstream compresses them
  well, which is why no compression happens at the dump stage.
- Disk: a dumped stack needs room for the uncompressed dump in Watchtower's temp directory, the
  same bytes in the helper container's writable layer, and the compressed archive spool.
- Role password hashes are in the archive, as `PGDATA` was; set an encryption passphrase.
- An **older** Watchtower restoring a v2 archive sees `_dumps` as an unknown volume, skips it with a
  warning, and leaves the database's current contents in place — the manual `psql` replay in
  `docs/backups.md` is the remedy. A v1 archive restores in the new code exactly as before.
- Other engines (MySQL/MariaDB, MongoDB) fit the same `DumpEngine` + label shape and would extend
  this ADR rather than replace it.
