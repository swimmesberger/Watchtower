# Stack backups

Watchtower can back up each stack's **named volumes** — the only state on the host that is not
reproducible from git and a registry — to external storage, on a cron schedule (once a day, twice a
day, every six hours — per instance, overridable per stack), with retention and optional encryption.
Design and rationale: [ADR-0016](decisions/0016-stack-backups.md) and
[ADR-0018](decisions/0018-cron-backup-schedule.md) (schedule).

What a backup is: one `*.tar.gz` (or `*.tar.gz.enc` when encrypted) per stack per run, containing
every volume labelled with the stack's compose project plus a `backup-manifest.json` that records
the instance, stack, volumes and timestamp — so any archive identifies itself even after being
copied around. **Postgres** services are the exception: they are dumped with `pg_dumpall` while
running, and the dump (`backup/_dumps/{service}.sql`) replaces the file snapshot of their data
volume ([ADR-0017](decisions/0017-database-aware-dumps.md)).

What it is **not**: incremental backups, or dumps for engines other than Postgres (the
`watchtower.backup.dump` label and the manifest's `engine` field are shaped so MySQL/MongoDB can
follow). Everything that is not a Postgres data volume is a file-level snapshot, taken with the
containers that mount it stopped.

## Setting it up

Everything global lives in **Settings → Backups** (or the `WATCHTOWER__BACKUP__*` environment
variables — env vars pin their setting read-only in the UI, see
[ADR-0014](decisions/0014-env-wins-runtime-settings.md)):

| Setting | Env var | Meaning |
| --- | --- | --- |
| Schedule | `WATCHTOWER__BACKUP__ENABLED` | Master switch for scheduled runs (default off). |
| Schedule expression | `WATCHTOWER__BACKUP__CRON` | Five-field cron (`minute hour day-of-month month day-of-week`), server-local time — see [The schedule](#the-schedule). Default `30 3 * * *` (03:30 daily). |
| *(legacy)* Time | `WATCHTOWER__BACKUP__TIME` | Compatibility alias from before cron: a server-local `HH:mm` that reads as `M H * * *`. Keeps working; `CRON` wins when both are set, and the UI treats a pinned `TIME` as pinning the schedule. Prefer `CRON`. |
| Misfire grace | `WATCHTOWER__BACKUP__MISFIREGRACEMINUTES` | How old a window may be and still be run once when it is noticed late (restart, downtime). Default `60`; clamped to 2 … 1440. Not in the UI. |
| Instance name | `WATCHTOWER__BACKUP__INSTANCENAME` | Names this Watchtower in the storage layout and manifests. Set it explicitly in containers — the default (machine name) is the container id there. |
| Retention (days) | `WATCHTOWER__BACKUP__RETENTIONDAYS` | Delete backups older than N days after each successful run; `0` keeps forever (default 30). |
| Retention (count) | `WATCHTOWER__BACKUP__RETENTIONMAXCOUNT` | Keep at most N backups per stack; `0` unlimited. **Set this when the schedule runs more than once a day** — the age limit alone keeps runs × days archives. |
| Encryption passphrase | `WATCHTOWER__BACKUP__ENCRYPTIONPASSPHRASE` | When set, archives are encrypted (see below). |
| Helper image | `WATCHTOWER__BACKUP__HELPERIMAGE` | Image for the never-started helper container (default `busybox:stable`); any pullable image works. |
| Provider | `WATCHTOWER__BACKUP__PROVIDER` | `sftp` (default) or `local`. |

Then opt each stack in on its **Backups tab**: include it in the schedule, optionally give it a
**schedule override** (its own cron expression instead of the instance one), and choose whether its
**stateful containers are stopped during the snapshot** (default on). "Stateful" means: the
containers that mount one of the volumes being archived — typically just the database; a stateless
web or api container that mounts nothing stays up. Dependents are stopped before the services they
`depends_on` and restarted in the opposite order, and the stop window covers only the local
snapshot, not the upload. With the switch off nothing is stopped and a write-active volume may be
captured mid-write. "Back up now" works regardless of the schedule switch.

Backups run one at a time through a single-flight queue, and every run is recorded in the tab's
history (status, size, remote path, full log).

### The schedule

The schedule is a classic five-field cron expression — `minute hour day-of-month month day-of-week`
— read as **server-local wall-clock time** (the host's time zone, DST included; a local time that a
DST jump skips does not fire). Lists, ranges, steps and names work (`30 3,15 * * *`, `0 */6 * * *`,
`0 2 * * MON-FRI`); the Quartz extensions (`L`, `W`, `#`) and a seconds field do not. The Settings
field previews what an expression means ("Every day at 03:30 and 15:30"); anything it cannot put
into words is shown as entered and is still valid if the server accepts it. Some shapes:

| Expression | Meaning |
| --- | --- |
| `30 3 * * *` | every day at 03:30 (the default) |
| `30 3,15 * * *` | every day at 03:30 and 15:30 |
| `0 */6 * * *` | every 6 hours, on the hour |
| `0 2 * * 1-5` | weekdays at 02:00 |
| `0 4 1,15 * *` | the 1st and 15th of every month at 04:00 |

Each stack follows the instance expression unless its Backups tab sets an **override**; the
override replaces the instance expression for that stack only (the master switch and the per-stack
opt-in still apply). Invalid expressions are rejected on save with the reason.

**How a window fires, and what happens when Watchtower was not running.** Every minute the scheduler
(an Elarion scheduled job) works out, per opted-in stack, the latest window of its expression that
is not older than the **misfire grace** (default 60 minutes) and newer than the last window it ran
for that stack — the last window is stored per stack, so a restart can tell "already ran" from
"slept through it". If there is one, the stack is enqueued once (the queue coalesces a stack that
is still waiting from the previous window). A window that opened while Watchtower was down, while
the master switch was off, or before the stack opted in, therefore **runs once if it is less than
the grace old and is skipped otherwise** — the log names the first skipped window. Only the latest
late window ever runs: a day of missed six-hourly windows becomes one backup, not four. Set
`WATCHTOWER__BACKUP__MISFIREGRACEMINUTES` higher if the host is routinely down across its window,
or to `2` (the minimum) if a late window should never run at all.

The combination to watch is **several runs a day × the age limit**: with `RetentionDays=30` and
two windows a day, retention keeps 60 archives per stack. Set **Retention (count)** as well.

### SFTP (e.g. a Hetzner Storage Box)

Any SSH-reachable storage works — a [Hetzner Storage Box](https://www.hetzner.com/storage/storage-box/),
a NAS, another server:

- **Host / port / username** — for a Storage Box: `u123456.your-storagebox.de`, port **23**,
  user `u123456` (or a sub-account limited to its own directory, recommended).
- **Auth** — password and/or an SSH private key (paste the full key block; register the matching
  public key with the storage). Watchtower accepts **Ed25519, ECDSA and RSA** keys in OpenSSH, PEM
  or PuTTY (`.ppk`) format — Ed448 is not supported. For Hetzner Storage Boxes SSH keys must be
  **RSA or ECDSA** (ed25519 is supported on newer boxes); generate e.g.
  `ssh-keygen -t ecdsa -b 521 -f storagebox_key`.
- **Base directory** — remote directory the layout is rooted in (default `watchtower-backups`),
  created automatically.

Save, then hit **Test storage** — it writes and deletes a probe file, so connectivity, auth and
write permission fail there with the server's own words rather than in a 03:30 run nobody watches.

The remote layout is `{base}/{instance}/{stack}/{project}_{yyyyMMddTHHmmssZ}.tar.gz[.enc]` — which
instance and stack a file belongs to is readable straight off its path, and the UTC timestamp in the
name is what retention works from.

### Local directory

The `local` provider writes the same layout under a directory *inside the Watchtower container* —
mount a second disk or network share there (e.g. `-v /mnt/backup-disk:/backups`). Backups on the
same disk as `/data` protect against very little.

## Per-service labels

Three compose labels, set on a **service**, refine what a run does. They are read from the running
containers' labels, so they take effect on the next run after a deploy — no Watchtower setting to
change.

| Label | Values | Effect |
| --- | --- | --- |
| `watchtower.backup.exclude` | `true` | The service's volumes are left out of the archive and it is never stopped. A volume is only excluded when **every** service mounting it is excluded — a volume shared with a non-excluded service is still archived (the run log says so), because dropping it would silently lose the other service's data. A volume no container mounts is always archived. |
| `watchtower.backup.stop` | `true` / `false` | Overrides the mount-based decision for this service: `false` keeps it running even though it mounts an archived volume (the log then warns that this volume's snapshot is only crash-consistent); `true` stops it although it mounts nothing archived. Does not override the stack's master switch. |
| `watchtower.backup.dump` | `false` / `postgres` | `false` opts a Postgres service out of dumps — its data volume is snapshotted like any other (and it is stopped like any other stateful container). `postgres` marks a service whose image is not in the detection list (see below) as a Postgres to dump. |

Unrecognised values are ignored with a `WARNING:` line in the run log.

```yaml
services:
  api:
    image: ghcr.io/acme/api
    depends_on: [db]
  db:
    image: postgres:16
    volumes: [pgdata:/var/lib/postgresql/data]
  cache:
    image: redis:7
    volumes: [redisdata:/data]
    labels:
      watchtower.backup.exclude: "true"   # a cache is not worth backing up
volumes:
  pgdata:
  redisdata:
```

Here a run dumps `db` (not stopped), skips `redisdata`, and stops nothing — `api` mounts no
volume. The `cache` service is excluded from stopping too.

## Database-aware dumps (Postgres)

A service is treated as Postgres when its image's repository name — the last path segment, tag
and registry stripped, compared exactly — is one of `postgres`, `postgresql`, `postgis`,
`pgvector`, `timescaledb`, `timescaledb-ha`, `pgautoupgrade` (so `postgres:16-alpine`,
`bitnami/postgresql`, `postgis/postgis`, `registry.example.com:5000/mirror/postgres` match;
`postgrest/postgrest` and `prometheuscommunity/postgres-exporter` deliberately do not — a false
positive would skip a real volume snapshot). Anything else can be opted in with
`watchtower.backup.dump: postgres`.

For each detected, **running** Postgres the run

- proves it can reach the cluster *before* anything is stopped (`psql -tAc "select 1"` as
  `POSTGRES_USER`, default `postgres`; retried once as the `postgres` OS user for peer-auth setups;
  `POSTGRES_PASSWORD` is read from the container's environment and handed back as `PGPASSWORD`,
  never logged) — a failure here fails the run without any downtime;
- leaves the container running, and excludes the volume mounted at `PGDATA` (the env var, else
  `/var/lib/postgresql/data`, or `/bitnami/postgresql`) from the file snapshot; any other volume the
  container mounts is archived as usual;
- runs `pg_dumpall --clean --if-exists` inside the stop window (after the stack's other stateful
  containers are stopped, so dump and file snapshot describe one state) and streams the SQL into
  `backup/_dumps/{service}.sql`;
- records the dump in the manifest (`dumps[]`: service, engine, file, image, user, the volumes it
  covers, the databases it contained, size) and bumps `formatVersion` to `2` — only archives with
  dumps carry v2; a stack without Postgres produces the same archive as before.

A container that is not running (exited, paused, restarting) falls back to the volume snapshot
with a `WARNING:`. A **failing `pg_dumpall` fails the run** — no silent fallback to a hot copy of a
live data directory; the previous archive is still on the storage.

Costs and caveats: the dump is uncompressed SQL until the archive's gzip — a dumped stack needs
temp space for the raw dump (Watchtower's temp dir and, briefly, the helper container's writable
layer) on top of the compressed spool. The dump contains role password hashes, exactly as the data
volume did: **set an encryption passphrase**. Replaying a dump from a newer Postgres major into an
older one may fail — pin the image tag.

## Encryption

With a passphrase set, every archive is piped through AES-256-CBC in the **OpenSSL `enc` container
format** (PBKDF2-SHA256, 600 000 iterations, random per-file salt). Nothing but stock OpenSSL is
needed to decrypt — deliberately, so a restore never depends on a running Watchtower:

```bash
openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -md sha256 \
  -in web-app_20260817T033000Z.tar.gz.enc -out web-app_20260817T033000Z.tar.gz
```

Keep the passphrase somewhere safe *outside* the host (password manager). Without it, encrypted
backups are unrecoverable; changing it affects future backups only. Note the format encrypts but
does not authenticate (no MAC) — treat the storage as untrusted for confidentiality, not integrity.

## Restoring a backup

### From the UI (same host, Watchtower running)

On the stack's **Backups tab → Restore…**: pick an archive (the list shows what is actually on the
storage right now, not the local history), then confirm by typing the stack's name. Watchtower then

1. downloads the archive (and decrypts it with the configured passphrase),
2. compares its contents against the stack's volumes — only volumes present in **both** are
   touched; mismatches are logged, never guessed at (if the stack has no volumes yet, deploy it
   once first so compose creates them),
3. checks every dump in the archive against the stack *before touching anything* — a dump whose
   Postgres service no longer exists, or whose file is missing from the archive, refuses the
   restore; a stopped Postgres container is started and waited for (`pg_isready`),
4. stops the containers that mount a restored volume — **every** running container when the archive
   carries a dump, since a stateless api would otherwise reconnect while `--clean` drops and
   recreates its database (dependents first; the Postgres being replayed stays up, as do services
   labelled `watchtower.backup.stop=false` or `watchtower.backup.exclude=true`) — then **erases the
   target volumes' current contents** and extracts the archive back into them (ownership and
   permissions preserved),
5. with those containers still down, replays each dump into its running Postgres: other sessions are
   terminated (`--clean` cannot drop a database under a live connection), the SQL is copied into the
   container and run with `psql -f`, and the run succeeds only if every database the manifest lists
   exists afterwards — `psql` diagnostics are counted and reported (some are expected from
   `--clean`, e.g. `role "postgres" already exists`), missing databases fail the restore. A dump file
   the manifest does not describe is still replayed (service taken from its file name) with a
   warning; a Postgres container that had to be started for the replay is left running,
6. restarts what it stopped, in dependency order.

Data written since the backup was taken is gone afterwards — that is the point, and why the confirm
is typed. The run lands in the same history as backups (trigger `restore`), with the full log.
Restores are refused while a deploy or another backup/restore of the stack is in flight. The volume
wipe is the one step that executes code in the helper container, so the helper image must provide
`sh`/`rm` — the default busybox does.

### Manually (disaster recovery — any Docker host, no Watchtower needed)

1. **Fetch** the archive (any SFTP client; `scp -P 23 u123456@u123456.your-storagebox.de:watchtower-backups/nas/web-app/web-app_20260817T033000Z.tar.gz.enc .`).
2. **Decrypt** if needed (command above).
3. **Inspect** — the tar contains `backup/backup-manifest.json` plus one directory per volume:

   ```bash
   tar -tzf web-app_20260817T033000Z.tar.gz | head
   # backup/backup-manifest.json
   # backup/_dumps/db.sql            (only when a Postgres was dumped; its data volume is absent)
   # backup/web-app_uploads/...
   ```

4. **Stop the stack** (`docker compose -p web-app down` — or the stack's Stop in Watchtower), so
   nothing writes into the volumes while you restore. If the volumes are damaged or you are on a
   fresh host, recreate them empty first (deploy the stack once, or `volumes.recreate`).
5. **Unpack each volume** into its (empty) named volume — the volume names are in the manifest:

   ```bash
   docker run --rm -i -v web-app_pgdata:/restore busybox \
     sh -c 'rm -rf /restore/* /restore/..?* /restore/.[!.]*; tar -xzf - -C /restore --strip-components=2 backup/web-app_pgdata' \
     < web-app_20260817T033000Z.tar.gz
   ```

   (`--strip-components=2` drops the `backup/{volume}/` prefix; the `rm` clears a non-empty volume.)
   Repeat per volume.
6. **Replay each dump** (`backup/_dumps/*.sql`, listed in the manifest's `dumps`) into the running
   Postgres container — start only the database, keep the rest of the stack down so `--clean` can
   drop and recreate the databases:

   ```bash
   tar -xzOf web-app_20260817T033000Z.tar.gz backup/_dumps/db.sql > db.sql
   docker exec -i web-app-db-1 psql -U postgres -d postgres < db.sql
   ```

   (`-i` feeds the SQL over stdin. Errors such as `role "postgres" already exists` are expected from
   a `--clean` dump; check afterwards that every database is present.)
7. **Start the stack again** (deploy from Watchtower). Verify the application actually sees its
   data before deleting anything remote.

Restoring a v2 archive (one with `_dumps/`) with an **older Watchtower** restores the volumes but
logs `_dumps` as an unknown volume and leaves the database's current contents in place — replay the
dump by hand as in step 6.

A single volume downloaded from the UI (Volumes → ⋯ → *Download archive*) has the same shape
(`backup/{volume}/…`, no manifest) and restores with the same command.

Moving to a **new host**: register the stack in the new Watchtower, deploy it once (creates the
volumes), set the backup storage + instance name to the old values, then use the UI restore — the
picker lists the old instance's archives as long as the instance name matches its directory.

## How a run works (and its costs)

1. Volumes are resolved by the `com.docker.compose.project` label — an **undeployed stack has no
   volumes and the run fails** with a message saying so. The project's containers are listed with
   their labels and mounts; Postgres services are detected, `watchtower.backup.*` labels applied,
   and the plan is fixed: which volumes are archived, which containers are stopped and in what
   order (`depends_on` dependents first; no labels → Docker's order; a cycle → a warning and
   Docker's order). The log prints it.
2. Each Postgres to be dumped is preflighted (reachability, auth) — still nothing stopped.
3. If "stop stateful containers" is on, the planned containers are stopped, in order. If a stop
   fails part-way, what was already stopped is restarted before the run fails.
4. Each Postgres is dumped with `pg_dumpall` into a temp file.
5. A helper container is *created but never started* with each archived volume mounted read-only;
   the manifest and the dumps are copied in, and the Docker daemon's archive endpoint streams one
   tar out of it (no code executes in the helper). The tar is gzipped (and encrypted) into a spool
   file in the container's temp directory — so the host needs free space for one compressed
   archive (plus the raw dumps, briefly), and the stop window ends here.
6. Containers restart in reverse stop order, then the spool uploads to the storage provider (to a
   `.partial` name, renamed on completion — a torn upload never looks like a finished backup).
7. Retention prunes the stack's remote folder: only files matching Watchtower's own naming pattern
   are considered, and the newest backup is never deleted. Archives are ordered by the
   second-resolution timestamp in their name, so several runs on one day are separate archives and
   the count limit counts *runs* (the age limit alone keeps runs × days of them).

Failures (including "process restarted mid-run") land in the history as `failed` with the log
attached; the next scheduled window simply tries again.

## The audit trail

Every run, restore, retention prune, storage test and configuration change is also recorded in the
global **Audit** page under the `backups` category — success or failure with the error message. The
run rows carry the settings in effect at the time (trigger, provider, encryption, how many
containers were stopped and volumes excluded, how many dumps were taken, retention), so "did last night's backup run, and was it encrypted back then?" is answered by the
trail even after the configuration has changed since. Retention there is bounded (newest 2000 audit
events per category); the per-stack Backups tab keeps the detailed per-run logs.
