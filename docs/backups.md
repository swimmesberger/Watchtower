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
containers that mount it **quiesced** — stopped (the default) or, opt-in, paused for the duration of
the tar ([ADR-0019](decisions/0019-pause-quiesce-and-parallel-stops.md), see
[Quiesce modes](#quiesce-modes-stop-or-pause)).

## Backing up Watchtower itself

Everything Watchtower knows — the stacks and their environment variables, templates, products and
releases, routes, realms, accounts, credentials, certificates and keys, the audit trail and the
metrics history — lives in the PostgreSQL it is configured against
([ADR-0024](decisions/0024-postgresql-only-and-state-in-the-database.md)). Backing up your stacks
without it restores their data but nothing that deploys them.

Watchtower backs that database up itself
([ADR-0027](decisions/0027-full-instance-backup-and-restore.md)). Under **Settings → Watchtower's own
database**, "Include in the backup schedule" (on by default) adds a `pg_dumpall` of it to the same
schedule your stacks run on, written to `{instance}/_watchtower/` on the same storage — so one folder
per instance holds the whole picture. "Back up Watchtower now" runs one immediately.

Two things to know:

- **An encryption passphrase is required.** The dump carries every database role's password hash, the
  data-protection key ring, the identity signing key and every certificate's private key. Without a
  passphrase set (see [Encryption](#encryption)) the schedule skips it and the button is disabled.
- **Nothing is stopped.** The dump is taken while Watchtower keeps serving — it has to be, since
  Watchtower is what runs it.

This needs your PostgreSQL to be **a container on the same Docker daemon**, which it is in the shipped
compose file. Watchtower finds it from its own connection string; if you run several database
containers and it picks wrong (or cannot choose), name the right one in **Database container** or
`WATCHTOWER__BACKUP__SELFPOSTGRESCONTAINER`. A managed PostgreSQL (RDS, Neon, a host-installed
server) cannot be dumped this way — back it up with whatever your provider offers.

### The full backup bundle

"Build bundle" (same card, admins only) produces **one file** holding a fresh dump of this database, the
newest archive of every stack, and the secrets that live outside the database — everything a new
Watchtower needs to become this one. Take one before migrating to a new host, and keep it wherever you
keep passwords.

```
bundle-manifest.json     ← which Watchtower wrote it, against which schema, and every archive's SHA-256
secrets.json             ← key-protection secret, backup passphrase, storage credentials
watchtower/watchtower_20260826T033000Z.tar.gz.enc
stacks/prod/blog/blog_20260826T033000Z.tar.gz.enc
stacks/prod/shop/globex/shop-globex_20260826T033100Z.tar.gz.enc
```

It is a plain (uncompressed) tar — its members are already compressed and encrypted — so `tar -tf` lists
it and `tar -xf` unpacks it anywhere. Each stack archive keeps the path it had on the backup storage, so
a restore can put it back exactly where the restored database expects to find it.

> **The bundle is the instance.** `secrets.json` holds the key-protection secret, the backup passphrase
> and your storage credentials in plain text — deliberately, because a bundle that restores into an
> instance whose certificates and keys are unreadable is not a backup. Treat the file as a credential.

A stack that has never been backed up appears in the manifest with no archive: its *definition* comes
back with the database, its *data* does not. The card says how many, so you can back those up and build
again.

The bundle is kept in Watchtower's own container and one is staged at a time, so it is lost on restart —
download it when it is ready, or build a fresh one later.

### Restoring a whole instance

**Settings → Watchtower's own database → Restore this Watchtower…** takes a bundle and makes this
Watchtower into the one it came from. Before touching anything it checks the bundle and refuses on:

- a bundle written by a **newer Watchtower** than this one — a database only ever migrates forward, so
  update this instance first;
- a **key-protection secret** that does not match. The certificates, ACME account key and signing key in
  the bundle are encrypted under the source instance's
  `WATCHTOWER__AUTH__KEYPROTECTIONSECRET`; set that variable to the value in the bundle's
  `secrets.json` and restart Watchtower before restoring. It cannot be changed while running;
- an archive that is missing, that does not match its checksum, or that the bundle's own passphrase
  cannot open.

It warns — but does not stop — when this Watchtower already manages stacks. Restoring replaces its whole
database; the containers it deployed keep running, unmanaged, until the checklist redeploys them.

The restore itself takes a few seconds: a helper container stops Watchtower, replays the dump, and
starts it again. It takes a safety dump of the current database first and replays that back if the
restore's own replay fails, so a failed restore leaves the instance as it was. The page waits for
Watchtower to come back and sends you to the sign-in form — **sign in with an account from the instance
the bundle came from**; the accounts this Watchtower had are gone with its database.

Watchtower must be running as a container on the same Docker daemon for this: it is stopped and started
around the replay. If it is not, restore the dump by hand (below).

#### Bringing the stacks back

After a restore, Settings shows a checklist of every stack in the restored database. Each one is
**deployed from git and then restored from its newest archive**, in that order — only the deploy creates
the volumes the restore needs, and a deploy on its own leaves the stack running on empty ones. Do them
one at a time or press **Revive all**; a stack you are handling yourself can be skipped, and the whole
checklist dismissed when you are done. What happened stays in the audit trail.

### Restoring it by hand

The archive is an ordinary Watchtower archive: decrypt it with stock OpenSSL as under
[Restoring a backup](#restoring-a-backup), and `backup/_dumps/watchtower.sql` inside it is a
`pg_dumpall` script.

```bash
openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -md sha256 \
  -in watchtower_20260826T033000Z.tar.gz.enc -pass pass:'YOUR PASSPHRASE' \
  | tar -xzO backup/_dumps/watchtower.sql > watchtower.sql
docker compose exec -T postgres psql -U watchtower -d postgres < watchtower.sql
```

Then start Watchtower: it migrates on startup, so a dump from an older version comes forward on its
own. **Carry `WATCHTOWER__AUTH__KEYPROTECTIONSECRET` across with it.** The certificates, the ACME
account key, the internal CA's signing key and the identity-assertion signing key are encrypted in the
database under that secret, and an instance restored without it throws on every one of them. It is an
environment variable, never stored in the database, and it cannot be changed at runtime. Only the ACME
material recovers by itself: an unreadable internal CA key is never replaced automatically, so a
deployment that uses port routes and loses the secret has to delete the `internal_cas` row and re-import
the new root on every device that trusted the old one.

(The `watchtower-data` volume is not part of this. It held the certificates and key ring before
ADR-0024; it holds nothing Watchtower needs now.)

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
| Stop grace | `WATCHTOWER__BACKUP__STOPTIMEOUTSECONDS` | How long a container *stopped* for the snapshot gets to exit on SIGTERM before SIGKILL (`docker stop -t`). Default `5` (the daemon's own default is 10); clamped to 1 … 300. Not in the UI. A service that needs longer belongs on a dump or on `pause`, not on a longer window. |
| Provider | `WATCHTOWER__BACKUP__PROVIDER` | `sftp` (default) or `local`. |
| Include Watchtower's own database | `WATCHTOWER__BACKUP__INCLUDESELF` | Adds a dump of Watchtower's own PostgreSQL to the schedule (default on). Needs an encryption passphrase; see [Backing up Watchtower itself](#backing-up-watchtower-itself). |
| Database container | `WATCHTOWER__BACKUP__SELFPOSTGRESCONTAINER` | Names the container holding Watchtower's own database, when detection cannot pick one. Blank = detect it. |

Then opt each stack in on its **Backups tab**: include it in the schedule, optionally give it a
**schedule override** (its own cron expression instead of the instance one), and choose whether its
**stateful containers are stopped during the snapshot** (the switch; default on) and, if so, **how**
— the **quiesce mode**: `stop` (default) or `pause`, see [Quiesce modes](#quiesce-modes-stop-or-pause).
"Stateful" means: the containers that mount one of the volumes being archived — typically just the
database; a stateless web or api container that mounts nothing stays up. Dependents go down before
the services they `depends_on` and come back in the opposite order — each dependency level at once,
so the window is the slowest container of a level rather than the sum — and the window covers only
the local snapshot, not the upload. With the switch off nothing is touched and a write-active volume
may be captured mid-write. "Back up now" works regardless of the schedule switch.

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
name is what retention works from. A **tenant** of a product gets one more level,
`{base}/{instance}/{product}/{tenant}/…`, so a fleet's archives group under the product they belong
to instead of scattering across one flat list of `{template}-{tenant}` directories.

**The directory is stamped once, when the stack is created, and does not move afterwards.** Renaming
a stack — or the Watchtower instance, or the product — therefore no longer orphans the archives
already written under the old path: Watchtower keeps reading and writing where the bytes actually
are. (Before this, every one of those renames silently stranded the history.) A stack created by an
older Watchtower has no stamp yet and keeps using the computed `{instance}/{stack}` path exactly as
it always did, until its next *successful* backup records that path on the stack. If you genuinely
want a stack's archives to move, move the files on the storage and clear the stack's stored
directory; nothing in Watchtower rewrites remote paths for you.

### Local directory

The `local` provider writes the same layout under a directory *inside the Watchtower container* —
mount a second disk or network share there (e.g. `-v /mnt/backup-disk:/backups`). Backups on the
same disk as `/data` protect against very little.

## Per-service settings: labels, or the Backups tab

Three settings, per **service**, refine what a run does: exclude it, how it is quiesced
(stop / pause / keep running), and whether it is dumped. They can be set two ways
([ADR-0020](decisions/0020-backup-service-settings-labels-win-ui-fills-gaps.md)):

- as **compose labels** (below) — infrastructure as code, versioned with the stack, read from the
  *running* containers so they take effect on the next run after a deploy; or
- as a **UI override** on the stack's Backups tab → *Services*: the same three knobs per service,
  stored in Watchtower, for a service that carries no label.

**A label always wins.** The Backups tab shows a labelled knob read-only with the label's text (the
same rule as env-pinned settings, [ADR-0014](decisions/0014-env-wins-runtime-settings.md)); the
override fills in only where no label is set, knob by knob. The *Services* table is also the place to
see what the **next run would actually do** — per container: stop / pause / keep / dump / excluded,
why, and whether that came from the mount rule, a label or an override — plus the planner's warnings,
so a typo'd label shows up before 03:30. "Your overrides as compose labels" renders the overrides as a
snippet to paste into the compose file; paste it, redeploy, clear the overrides, and nothing about
the run changes — that is how a setting tried out in the UI becomes code.

### The labels

| Label | Values | Effect |
| --- | --- | --- |
| `watchtower.backup.exclude` | `true` | The service's volumes are left out of the archive and it is never stopped. A volume is only excluded when **every** service mounting it is excluded — a volume shared with a non-excluded service is still archived (the run log says so), because dropping it would silently lose the other service's data. A volume no container mounts is always archived. |
| `watchtower.backup.stop` | `true` / `false` / `pause` | Overrides the mount-based decision for this service: `false` keeps it running even though it mounts an archived volume (the log then warns that this volume's snapshot is only crash-consistent); `true` **stops** it (also when the stack's quiesce mode is `pause`), even if it mounts nothing archived; `pause` **pauses** it for the snapshot instead of stopping it (crash-consistent, see [Quiesce modes](#quiesce-modes-stop-or-pause)), even if it mounts nothing archived. Does not override the stack's master switch; on restore `pause` reads as `true`. |
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

## Quiesce modes: stop or pause

A container that mounts a volume being archived has to hold still while the tar reads it. There are
two ways to make it, chosen per stack (**Quiesce mode** on the Backups tab, default `stop`) and
overridable per service with the `watchtower.backup.stop` label
([ADR-0019](decisions/0019-pause-quiesce-and-parallel-stops.md)):

| Mode | What happens | Consistency | Downtime |
| --- | --- | --- | --- |
| `stop` (default) | `docker stop` with a 5 s SIGTERM grace (`WATCHTOWER__BACKUP__STOPTIMEOUTSECONDS`), tar, `docker start`. | **Application-consistent** — the process flushed and exited cleanly. | Stop grace + tar + cold start; the grace is paid once per dependency level, not per container. |
| `pause` | `docker pause` (cgroup freezer — the processes are suspended in milliseconds, nothing exits, TCP connections stay open), tar, `docker unpause`. | **Crash-consistent only** — whatever the application still held in userspace buffers (a database's unflushed pages, a half-written upload) is *not* in the snapshot; it is the "pulled the plug" state that WAL/redo-log engines recover from on the next start, and that a plain file tree simply reflects as "the file as it was a moment ago". | Tar duration only, typically a few seconds; no restart, clients see a stall rather than a disconnect. |

Use `pause` for file volumes — uploads, media, generated assets — and for services whose restart is
the expensive part. Keep `stop` (or a per-service `stop: true`) for a database that is **not** dumped
(MySQL/MariaDB, MongoDB, SQLite inside an app), unless you have verified it recovers cleanly from a
crash-consistent copy. Postgres is dumped and never quiesced either way.

Whatever the mode, the window covers the local snapshot only, and the run brings everything back
even when it fails or is cancelled — a stop that fails part-way restarts what was already down, a
paused container is unpaused in a `finally`. There is a second safety net for pauses: before pausing
anything, the run records the containers in Watchtower's database, and on every start Watchtower
unpauses whatever a previous process left paused (a crash mid-window must not leave a stack frozen);
the event shows up in the audit trail as `reconcile.unpause`.

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
  covers, the databases it contained, size). The manifest's `formatVersion` is written
  unconditionally (currently 3 — see the version history above), so a Postgres-less stack's archive
  differs from a pre-v2 one only by the manifest's version and its product/tenancy/release keys.

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
3. If "stop stateful containers" is on, the planned containers are quiesced — stopped (5 s SIGTERM
   grace) or paused, per the stack's quiesce mode and the labels — one dependency level at a time,
   concurrently within a level. Planned pauses are recorded in the database first (the startup safety
   net). If a level fails part-way, everything already down is restarted/unpaused before the run fails.
4. Each Postgres is dumped with `pg_dumpall` into a temp file.
5. A helper container is *created but never started* with each archived volume mounted read-only;
   the manifest and the dumps are copied in, and the Docker daemon's archive endpoint streams one
   tar out of it (no code executes in the helper). The tar is gzipped (and encrypted) into a spool
   file in the container's temp directory — so the host needs free space for one compressed
   archive (plus the raw dumps, briefly), and the stop window ends here.
6. Containers come back in reverse level order — stopped ones are restarted, paused ones unpaused,
   and the pause records are cleared — then the spool uploads to the storage provider (to a
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
containers were paused and how many stopped, how many volumes excluded, how many dumps were taken,
retention), so "did last night's backup run, and was it encrypted back then?" is answered by the
trail even after the configuration has changed since. A `reconcile.unpause` row means a previous
process died inside a pause window and its containers were thawed on the next start. Retention there is bounded (newest 2000 audit
events per category); the per-stack Backups tab keeps the detailed per-run logs.
