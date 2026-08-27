# ADR-0027: Watchtower backs itself up, and a bundle restores it somewhere else

- Status: Accepted (implemented)
- Date: 2026-08-26
- Related: [ADR-0016](0016-stack-backups.md) (the stack backup machinery this reuses whole),
  [ADR-0017](0017-database-aware-dumps.md) (the `pg_dumpall` path it points at Watchtower's own
  database), [ADR-0018](0018-cron-backup-schedule.md) (the schedule it joins),
  [ADR-0024](0024-postgresql-only-and-state-in-the-database.md) (why the database *is* the instance),
  [docs/backups.md](../backups.md) (the operator-facing description).

## Context

Since ADR-0024 every fact Watchtower owns lives in one PostgreSQL database: the stacks and their
environment variables, the templates, products and releases, the routes and their access grants, the
accounts and sessions, and — after decision 4 of that ADR — the certificates, the ACME account key,
the identity signing key and the data-protection key ring. The container's `/data` volume holds
nothing Watchtower needs any more.

Against that, the backup feature covered **only stacks**. An operator with a nightly schedule and a
year of archives could restore every stack's volumes onto a new box and still have nothing that knew
how to deploy them: no repository URLs, no environment variables, no routes, no accounts. ADR-0024
noted in passing that backing up Watchtower's own state "becomes a PostgreSQL concern"; nothing was
built, and `docs/backups.md` told operators to run `pg_dump` by hand — next to a stale paragraph
telling them to also keep the `watchtower-data` volume, which by then held nothing.

Three things made this awkward rather than obvious:

1. **Watchtower cannot back itself up through its own stack machinery.** `SelfProjectNameProvider`
   reserves Watchtower's own compose project against stack use (so no stack can read its containers
   through the App API), which also means the database cannot be registered as a stack and dumped
   like any other.
2. **A dump of this database is not an ordinary archive.** `pg_dumpall` carries every role's password
   hash, and the tables carry the data-protection key ring, the signing key and every certificate's
   private key. It is the instance.
3. **Restoring it cannot be done by the process that holds it open.** `pg_dumpall --clean` drops and
   recreates every database after terminating every other session — including Watchtower's own EF
   connection pool, which would immediately reconnect into the middle of the replay.

## Decision

### 1. A scheduled, always-encrypted dump of Watchtower's own database, beside the stack archives

`InstanceBackupService` takes a `pg_dumpall` of Watchtower's database over the Docker exec API,
wraps it in the same archive format, gzip and OpenSSL-compatible encryption a stack backup uses, and
uploads it through the same `IBackupStorage` to `{instance}/_watchtower/watchtower_{ts}.tar.gz.enc`
— a sibling of the stack directories under the same instance root. **One storage folder per instance
therefore holds everything a rebuild needs.** The same retention applies, through the same
`BackupRetentionRunner` the stack runs now use.

Consequences of the "same everything" choice, each deliberate:

- **The archive carries no volumes.** Since ADR-0024 there is no file state to snapshot, and
  `BackupArchiveService` already supported a dumps-only archive (a stack whose only state is a dumped
  database produces one).
- **Nothing is stopped or paused.** The dump is consistent by construction, which is what lets
  Watchtower keep serving through its own backup — as it must, being the thing running it.
- **Encryption is mandatory, not optional as it is for a stack.** A run without a passphrase is
  refused rather than silently downgraded, for the reason in context §2.
- **It shares the single-flight backup queue.** An instance dump waits behind a large stack backup,
  which is the right way round: a queued dump is a delayed dump, whereas two runs racing for the
  spool disk is a failed one.

The schedule is the instance-wide cron (there is one instance, so there is nothing to override it
with), governed by `Watchtower:Backup:IncludeSelf` (default on). Its cursor is a settings row rather
than a column, since there is no instance table to put one on; it is read through the settings
manager rather than the options snapshot, so a value written last tick is certainly seen this tick.
A window that opens with no passphrase configured is **skipped and logged, and the cursor still
moves** — the alternative is a run that fails every night, and a window that re-fires every minute.

### 2. `BackupEvent.StackId` becomes nullable rather than growing a parallel table

An instance run has no stack. The history views, the single-flight queue, the retention pass and the
startup sweep all already speak `BackupEvent`, and a second table would have duplicated every one of
them. The wire DTO gains a `kind` (`stack` | `instance`) derived from the null, so the UI branches on
a word rather than on an absence, and `backups.events` gains an optional `kind` filter. Unfiltered
history is unchanged and still returns both — an instance run is part of "what has this Watchtower
been backing up".

The stack relationship still cascades, so a deleted stack takes its own history with it; only the
stackless rows outlive every stack.

`_watchtower` is refused as a stack name (`BackupNaming.IsReserved`, checked where the compose
project name already is), because a stack sanitizing onto it would write its archives into the
instance directory, and retention prunes a *directory*.

### 3. Finding the database is a detection with a loud failure, not a configuration

`SelfPostgresLocator` parses Watchtower's own connection string and looks for a running PostgreSQL
container that answers to its `Host` — by compose service, container name, or the
`{project}-{service}-{replica}` name Compose generates — among the containers of Watchtower's own
compose project, or among all running containers when it is not under Compose. One unmatched
candidate still wins (a service aliased differently from the host is ordinary); several do not, and
the run fails naming them, because the loser would be dumped and the dump would look healthy.
`Watchtower:Backup:SelfPostgresContainer` is the override.

A managed or host-installed PostgreSQL has no container to exec into. That fails **loudly**, with a
message that says so and also admits it is what an unreachable daemon looks like from here: a
self-backup that quietly does nothing is invisible until the day it is needed.

### 4. An exportable bundle carries an instance to another machine

An admin can export one plain (uncompressed) tar containing the fresh instance archive, the newest
archive of every stack, a `bundle-manifest.json` and a `secrets.json`. Plain tar because its members
are already compressed and encrypted, and because the point of the artifact is to be handed to the
import on the other side.

`bundle-manifest.json` records `bundleFormatVersion`, the instance name, `appVersion`,
`lastMigrationId`, and per-archive sizes and SHA-256s. **`lastMigrationId` is what an import decides
on**, not the version string: migrations only roll forward, so "this binary knows that migration" is
exact where comparing versions guesses. The version string is for the operator's error message.

### 5. Restore runs from a sibling coordinator container

The running Watchtower pre-stages everything it still can — re-uploading the bundle's stack archives
to storage at their recorded paths, extracting the SQL into the database container, writing a nonce
into the database it is about to lose — then spawns a `--restore-self` coordinator from its own
image, modelled on the `--self-update` coordinator: Docker socket, no network, group ids from
`/proc/self/status`. The coordinator takes a safety dump, stops Watchtower, replays, and restarts it
in a `finally` whatever happened. On the way back up, the nonce's absence is what proves the replay
committed.

Validation happens **before** any of that, and refuses rather than warns on: a bundle whose
`lastMigrationId` this binary does not know, and a `KeyProtectionSecret` that differs from the one
this instance runs with. The second is the sharpest edge in the whole feature — the DB's protected
rows are AES-GCM under an env-only secret, so restoring without it yields an instance that throws on
every certificate and key it touches. The message names the variable and says it needs a restart.

### 6. Restore is offered after login, never anonymously

A fresh instance already creates a bootstrap admin (`AuthBootstrapService`) whose password is set by
env or printed once to the log. The restore wizard lives behind that login, and behind
`[RequireRole(Admin)]`, rather than on an anonymous "is this instance empty" endpoint: an unauthenticated
restore endpoint is an unauthenticated way to replace an instance, and "the instance looked empty" is
not an authorization decision. The wizard is offered on a fresh-looking instance and is also always
reachable from Settings.

After the restart, a recovery checklist walks the stacks: redeploy from git (the definitions are in
the restored database), then restore each stack's newest volume archive.

## Consequences

- **The bundle is radioactive, by design.** `secrets.json` carries the key-protection secret, the
  backup passphrase and the storage credentials in plain text, so that one artifact plus its
  passphrase is a complete instance. Export is admin-only and audited, and the UI says what the file
  is. This is a deliberate trade against the alternative — an operator who restores into a new box
  and discovers their certificates are unreadable because a secret they never knew about stayed
  behind.
- **The pg password reaches the restore coordinator as an env var on its create body**, visible to
  anyone who can `docker inspect` it. Accepted: that is anyone who already owns the Docker socket,
  and therefore the host.
- **Anyone who can place an instance archive on the backup storage can walk away with the
  instance.** `backups.runInstance` is admin-only for that reason, where the stack runs are not.
- **Restoring an older dump into a newer binary is the supported direction** and works by itself:
  migrations run on startup, so the restored schema rolls forward. The reverse is refused.
- **A restored instance carries stale coordination rows** — scheduler claims, role leases — and a
  rolled-back backup cursor. The completion pass clamps the cursors and bumps the routes version;
  the leases expire on their own.
- **`docs/backups.md`'s "Backing up Watchtower itself" section is replaced**, including the stale
  instruction to keep the `watchtower-data` volume for certificates and keys, which have been rows
  since ADR-0024.

## Alternatives considered

- **A second `instance_backup_events` table.** Rejected: it duplicates the queue, the sweep, the
  retention pass and both history views to avoid one nullable column.
- **Restoring from storage only, with no bundle.** Simpler, and still the right path for an instance
  restoring itself in place — but it requires the new box to already have the storage credentials and
  the passphrase, which is exactly what an operator rebuilding after a loss does not have to hand.
  The bundle exists to be the one thing they need. (Restore-from-storage remains a natural
  complement, not built.)
- **An anonymous first-run restore endpoint.** Rejected — see decision 6.
- **Registering Watchtower's own compose project as a stack.** Rejected: the project reservation
  exists to stop exactly that, and undoing it would hand any stack Watchtower's own containers.
